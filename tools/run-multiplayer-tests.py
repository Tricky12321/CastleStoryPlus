#!/usr/bin/env python3
"""Multiplayer checks with two players on this machine: a host and a client, each a test player of its own (a fresh
copy of a game root without saves, its own DevTools API port), joined over the loopback address into a new Invasion
game. The checks compare what the client has with what the host has, through the DevTools API:

    BricktronCap          the host's bricktron limit reaches the client, and the client's own game mode script does
                          not set it back (issue #87)
    ArmySelection         the task list's ranged row selects every ranged unit on the client, also when the client's
                          copies of their weapons say otherwise (issue #84)
    BlueprintDeliveries   material delivered to a blueprint reaches the client's blueprint (issue #83)
    WaveRows              the scoreboard's waves (enemies left of each wave) reach the client (issue #85)
    TaskCrew              the host's 12 bricktrons per task, chosen in the lobby, reach the client, and twelve of the
                          client's bricktrons put on its own task all join its crew there and on the host (issue #88)

    tools/run-multiplayer-tests.py [--filter Cap] [--port 27890] [--player DIR] [--root-src DIR] [--keep] [--hold N]

--port is the host's API port, the client's is the next one (never 27860, the player's own game). The players share
the player folder (default: the Unity 6 port's CastleStoryPlus.Player/Build/Linux) and start one after the other.
The results are printed and written to multiplayer-tests-<yyyy-MM-dd-HHmmss>.json in --work, next to the players'
logs. The exit code is 0 when every check passed.
"""
import argparse
import json
import os
import shutil
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
USER_PORT = 27860
GAME_PORT = 7790


def log(*parts):
    print(time.strftime("%H:%M:%S"), *parts, flush=True)


class Player:
    def __init__(self, args, name, port, stamp):
        self.args = args
        self.name = name
        self.port = port
        self.api = "http://127.0.0.1:%d" % port
        self.root = os.path.join(args.work, "root-%s-%s" % (stamp, name))
        self.log_file = os.path.join(args.work, "multiplayer-tests-%s-%s.log" % (stamp, name))
        self.process = None

    def call(self, method, path, timeout=30, **query):
        if query:
            path += ("&" if "?" in path else "?") + urllib.parse.urlencode(query)
        request = urllib.request.Request(self.api + path, method=method, data=b"" if method == "POST" else None)
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                return json.load(response)
        except urllib.error.HTTPError as e:
            raise RuntimeError("%s %s %s: %s" % (self.name, method, path, e.read().decode(errors="replace")[:300]))

    def answers(self):
        try:
            self.call("GET", "/api/status", 2)
            return True
        except Exception:
            return False

    def wait_for(self, what, check, timeout, interval=1.0):
        end = time.time() + timeout
        last = None
        while time.time() < end:
            if self.process is not None and self.process.poll() is not None:
                raise RuntimeError("%s exited while waiting for %s" % (self.name, what))
            try:
                if check():
                    return
            except (urllib.error.URLError, OSError, ValueError, RuntimeError) as e:
                last = e
            time.sleep(interval)
        raise RuntimeError("%s waited %d s for %s%s" % (self.name, timeout, what, (" (%s)" % last) if last else ""))

    def start(self):
        if self.answers():
            raise RuntimeError("another game answers on port %d" % self.port)
        shutil.copytree(self.args.root_src, self.root, symlinks=True)
        shutil.rmtree(os.path.join(self.root, "Info", "Saves"), ignore_errors=True)
        os.makedirs(os.path.join(self.root, "Info", "Saves"), exist_ok=True)
        env = dict(os.environ, CSP_GAME_ROOT=self.root, CSP_API_PORT=str(self.port))
        command = ["./run_bepinex.sh", "-force-glcore", "-screen-fullscreen", "0", "-screen-width", "1600", "-screen-height", "900", "-logFile", self.log_file]
        self.process = subprocess.Popen(command, cwd=self.args.player, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
        log("[%s]" % self.name, "player", self.process.pid, "port", self.port)
        self.wait_for("the DevTools API", self.answers, 300)
        self.wait_for("the title screen", lambda: self.call("GET", "/api/status").get("scene") == "SceneMenu", 300)

    def quit(self):
        if self.process is None:
            return
        try:
            self.call("POST", "/api/quit?save=true", 120)
        except Exception as e:
            log("[%s]" % self.name, "quit", repr(e))
        for _ in range(240):
            if self.process.poll() is not None:
                break
            time.sleep(0.5)
        if self.process.poll() is None:
            os.killpg(self.process.pid, signal.SIGTERM)
            time.sleep(10)
        if self.process.poll() is None:
            os.killpg(self.process.pid, signal.SIGKILL)
        if not self.args.keep:
            shutil.rmtree(self.root, ignore_errors=True)


def join(host, client):
    # The title screen answers before its menus are up: a moment more, or the host button is not taken.
    time.sleep(8)
    host.call("POST", "/api/net/host", port=GAME_PORT, name="Host")
    host.wait_for("the lobby", lambda: any(p.get("local") for p in host.call("GET", "/api/net").get("players", [])), 120)
    log("lobby:", host.call("POST", "/api/net/mode", gamemode="invasion"))
    # The checks use the API's cheats (spawns, enemies), off in every new lobby (CheatGuard).
    host.call("POST", "/api/net/cheats", value="true")
    # Chosen in the lobby, as the host's "Bricktrons per task" does (TaskCrew); the setting is saved in the player
    # folder's config, so it is put back afterwards.
    TASK_CREW_BEFORE.append(host.call("GET", "/api/net/taskcrew")["value"])
    log("bricktrons per task:", host.call("POST", "/api/net/taskcrew", value=TASK_CREW)["value"])
    client.call("POST", "/api/net/join", address="127.0.0.1", port=GAME_PORT, name="Client")
    host.wait_for("the client in the lobby", lambda: len(host.call("GET", "/api/net").get("players", [])) >= 2, 120)
    time.sleep(3)
    client.call("POST", "/api/net/ready", value="true")
    time.sleep(2)
    host.call("POST", "/api/net/start", button="true")
    for player in (host, client):
        player.wait_for("the game's introduction", lambda p=player: p.call("GET", "/api/dialog").get("open"), 600, 2)
    for player in (host, client):
        player.call("POST", "/api/dialog/press?index=0")


def check_bricktron_cap(host, client):
    host.call("POST", "/api/net/rules", bricktronCap=27)
    client.wait_for("the host's bricktron limit", lambda: client.call("GET", "/api/net/rules")["bricktronCap"] == 27, 15)
    # As the client's own game mode script does when its map is ready (Rules.bricktronCap = its settings' limit).
    answer = client.call("POST", "/api/net/rules", bricktronCap=15)
    if answer["bricktronCap"] != 27:
        return False, "the client's own limit replaced the host's: %d" % answer["bricktronCap"]
    return True, "client has the host's limit 27"


def ranged_count(player):
    return sum(1 for b in player.call("GET", "/api/bricktrons")["bricktrons"] if b["job"] in ("Archer", "Arbalist", "Artificer", "Alchemist"))


def check_army_selection(host, client):
    host.call("POST", "/api/cheat/bricktrons", count=6, job="archer")
    host.wait_for("the archers", lambda: ranged_count(host) >= 6, 30)
    expected = ranged_count(host)
    client.wait_for("the archers", lambda: ranged_count(client) == expected, 30)
    # The client's copies of the weapons are its own (the host never updates their ammunition): most of them empty.
    stale = client.call("POST", "/api/bricktrons/weaponammo", value=0, count=expected - 2, q="Archer")["changed"]
    selected = client.call("POST", "/api/select/army", ranged="true")["selected"]
    if selected != expected:
        return False, "the client's ranged row selected %d of %d ranged units (%d with empty weapons there)" % (selected, expected, stale)
    return True, "selected all %d ranged units (%d with empty weapons on the client)" % (selected, stale)


def place_warehouse(host):
    home = host.call("GET", "/api/ground?radius=0&heights=false")["center"]
    for dx, dz in ((-7, -12), (10, -12), (-12, 8), (12, 10), (0, 14), (0, -16), (16, 0), (-16, 0)):
        # On the empty voxel over the top of the ground.
        ground = host.call("GET", "/api/ground?radius=0&heights=false&x=%d&z=%d" % (home[0] + dx, home[1] + dz))
        if ground.get("min") is None or ground["min"] < 0:
            continue
        at = "%d,%d,%d" % (home[0] + dx, ground["min"] + 1, home[1] + dz)
        answer = host.call("POST", "/api/blueprints/place", key="Warehouse", at=at, commit="true")
        if answer.get("placed"):
            return at, answer["project"]["id"]
    raise RuntimeError("found no place for a warehouse round the home crystal")


def storage_objects(player):
    storages = player.call("GET", "/api/storage?q=Warehouse&empty=true")["storages"]
    return storages[0]["objects"] if storages else None


def check_blueprint_deliveries(host, client):
    at, project = place_warehouse(host)
    x, y, z = (int(v) for v in at.split(","))
    host.call("POST", "/api/cheat/items", key="Blocks/Stone_DoubleBrick", count=8, at="%d,%d,%d" % (x + 3, y, z + 6))
    host.call("POST", "/api/cheat/items", key="Blocks/Wood_SinglePlank", count=6, at="%d,%d,%d" % (x - 3, y, z + 6))
    host.call("POST", "/api/projects/crew", id=project, size=8)
    host.call("POST", "/api/projects/hire", id=project, count=5)
    host.wait_for("bricks and planks in the warehouse blueprint", lambda: len(storage_objects(host) or {}) >= 2, 240, 3)
    time.sleep(5)
    try:
        client.wait_for("the same material in its warehouse blueprint", lambda: storage_objects(client) == storage_objects(host), 30, 2)
    except RuntimeError:
        return False, "host %s, client %s" % (storage_objects(host), storage_objects(client))
    return True, "client's blueprint holds %s" % storage_objects(client)


def check_wave_rows(host, client):
    host.call("POST", "/api/cheat/enemies", type="corruptron", count=2, wave=7)

    def rows():
        values = client.call("GET", "/api/raids").get("hostWaveRows") or []
        return [values[i:i + 9] for i in range(0, len(values) - 8, 9)]

    try:
        client.wait_for("wave 7 on its scoreboard", lambda: any(r[0] == 7 and sum(r[1:5]) > 0 for r in rows()), 30)
    except RuntimeError:
        return False, "the client's scoreboard rows: %s (it has %d raids itself)" % (rows(), client.call("GET", "/api/raids")["count"])
    return True, "client shows %s" % rows()


TASK_CREW = 12

TASK_CREW_BEFORE = []


def own_projects(player, kind, everyone=False):
    return player.call("GET", "/api/projects", q=kind, all="true" if everyone else "false")["projects"]


def builders(player):
    return [b for b in player.call("GET", "/api/bricktrons")["bricktrons"] if b["job"] == "Builder"]


def check_task_crew(host, client):
    try:
        client.wait_for("the host's bricktrons per task", lambda: client.call("GET", "/api/net/taskcrew")["value"] == TASK_CREW, 15)
    except RuntimeError:
        return False, "the client has %s" % client.call("GET", "/api/net/taskcrew")
    # The client's own builders: the host's limit high enough, then the game's spawn command sent by the client (the
    # API's own spawn cheat is the host's; cheats are allowed in this game).
    host.call("POST", "/api/net/rules", bricktronCap=60)
    have = len(builders(client))
    if have < TASK_CREW:
        for _ in range(TASK_CREW - have):
            client.call("POST", "/api/net/cheatprobe", kind="spawn")
        client.wait_for("%d builders" % TASK_CREW, lambda: len(builders(client)) >= TASK_CREW, 30)
    before = set(p["id"] for p in own_projects(client, "Build"))
    client.call("POST", "/api/projects/create", type="build")
    client.wait_for("its new build task", lambda: len([p for p in own_projects(client, "Build") if p["id"] not in before]) > 0, 30)
    project = [p for p in own_projects(client, "Build") if p["id"] not in before][0]
    if project["maxCrew"] != TASK_CREW:
        return False, "the client's new task takes %d, not %d" % (project["maxCrew"], TASK_CREW)
    units = ",".join(str(b["id"]) for b in builders(client)[:TASK_CREW])
    client.call("POST", "/api/projects/hire", id=project["id"], units=units)

    def crew(player, everyone):
        # The host sees the client's task among every faction's; ids differ between the players.
        return max([p["crew"] for p in own_projects(player, "Build", everyone)] or [0])

    try:
        client.wait_for("a crew of %d on its task" % TASK_CREW, lambda: crew(client, False) == TASK_CREW, 20)
        host.wait_for("a crew of %d on the client's task" % TASK_CREW, lambda: crew(host, True) == TASK_CREW, 20)
    except RuntimeError:
        return False, "crew on the client %d, on the host %d (of %d put on it)" % (crew(client, False), crew(host, True), TASK_CREW)
    return True, "the client's task holds %d on both players" % TASK_CREW


CHECKS = [
    ("BricktronCap", check_bricktron_cap),
    ("ArmySelection", check_army_selection),
    ("BlueprintDeliveries", check_blueprint_deliveries),
    ("WaveRows", check_wave_rows),
    ("TaskCrew", check_task_crew),
]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--filter", default="", help="part of a check's name (empty: all)")
    parser.add_argument("--port", type=int, default=27890, help="API port of the host; the client's is the next one (never 27860)")
    parser.add_argument("--player", default=os.path.join(REPO, "CastleStoryPlus.Player", "Build", "Linux"))
    parser.add_argument("--root-src", default=os.path.expanduser("~/.cache/csp-bench/root-src"))
    parser.add_argument("--work", default=os.path.expanduser("~/.cache/csp-tests"), help="folder for the copies, logs and results")
    parser.add_argument("--keep", action="store_true", help="keep the game root copies afterwards")
    parser.add_argument("--hold", type=int, default=0, help="keep the players running this many seconds after the checks")
    args = parser.parse_args()
    if not os.path.isfile(os.path.join(args.player, "run_bepinex.sh")):
        sys.exit("no run_bepinex.sh in " + args.player)
    if args.port <= USER_PORT <= args.port + 1:
        sys.exit("port %d is the player's own game: choose another --port" % USER_PORT)
    checks = [(name, check) for name, check in CHECKS if args.filter.lower() in name.lower()]
    if not checks:
        sys.exit("no check matches " + repr(args.filter))
    stamp = time.strftime("%Y-%m-%d-%H%M%S")
    os.makedirs(args.work, exist_ok=True)
    host = Player(args, "host", args.port, stamp)
    client = Player(args, "client", args.port + 1, stamp)
    results = []
    try:
        # One after the other: they share the player folder's BepInEx config, which each writes while the mod loads.
        host.start()
        client.start()
        join(host, client)
        log("in the game")
        time.sleep(5)
        for name, check in checks:
            begun = time.time()
            try:
                passed, message = check(host, client)
            except Exception as e:
                passed, message = False, "error: %s" % e
            results.append({"name": name, "outcome": "Passed" if passed else "Failed", "message": message, "seconds": round(time.time() - begun, 1)})
            log("%-7s %-20s %5.1f s  %s" % (results[-1]["outcome"], name, results[-1]["seconds"], message))
        if args.hold > 0:
            log("holding the players for %d s" % args.hold)
            time.sleep(args.hold)
    finally:
        if TASK_CREW_BEFORE:
            try:
                host.call("POST", "/api/net/taskcrew", value=TASK_CREW_BEFORE[0])
            except Exception as e:
                log("[host]", "bricktrons per task not put back:", repr(e))
        client.quit()
        host.quit()
    path = os.path.join(args.work, "multiplayer-tests-%s.json" % stamp)
    with open(path, "w") as f:
        json.dump({"results": results}, f, indent=1)
    failed = [r for r in results if r["outcome"] != "Passed"]
    log("%d passed, %d failed; %s" % (len(results) - len(failed), len(failed), path))
    sys.exit(1 if failed or not results else 0)


if __name__ == "__main__":
    main()
