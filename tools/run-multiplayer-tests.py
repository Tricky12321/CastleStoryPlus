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
    Weather               the host's rain reaches the client (state, boost and what it shows), and its end too
    TaskCrew              the host's max of 12 bricktrons per task, chosen in the lobby, reach the client, and twelve of the
                          client's bricktrons put on its own task all join its crew there and on the host (issue #88)
    Demolition            the demolition task (#200): the host refuses a client's demolition of the host's building, the
                          host's demolition blueprint reaches the client, and the client's demolition of its own building
                          (and its cancel) is done by the host
    SavedTeams            three players (host, client and a third player) in a Conquest Versus lobby, two of
                          them in one team, are allied in the game; the game saved and loaded again in a new lobby puts both
                          back in the same team, and they are allied again
                          (issue #39). Runs last, in lobbies of its own: the other checks' game is left first

    tools/run-multiplayer-tests.py [--filter Cap] [--port 27890] [--player DIR] [--root-src DIR] [--keep] [--hold N]
                                   [--headless] [--no-save-copy]

--headless starts both players with -batchmode -nographics (no windows). A check that needs rendering is listed in
NEEDS_GRAPHICS and left out of a headless run (none does today: they all read state through the API).

--no-save-copy: SavedTeams copies the save to the clients' saves by default; with this option the game's own download of a
missing save to the clients is used instead (slower, and loading a downloaded save has not been verified).

--port is the host's API port, the client's is the next one, SavedTeams' third player the one after (never 27860, the player's own game). The players share
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
        if self.args.headless:
            command += ["-batchmode", "-nographics"]
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
    # The Essentials edition has no TaskCrewSize (no endpoint): its check is skipped.
    try:
        TASK_CREW_BEFORE.append(host.call("GET", "/api/net/taskcrew")["value"])
        log("bricktrons per task:", host.call("POST", "/api/net/taskcrew", value=TASK_CREW)["value"])
    except RuntimeError as e:
        if "No endpoint" not in str(e):
            raise
        log("bricktrons per task: not in this edition")
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
    host.call("POST", "/api/cheat/enemies", type="healtron", count=1, wave=7)

    def rows():
        values = client.call("GET", "/api/raids").get("hostWaveRows") or []
        return [values[i:i + 11] for i in range(0, len(values) - 10, 11)]

    try:
        client.wait_for("wave 7 on its scoreboard", lambda: any(r[0] == 7 and sum(r[1:6]) > 0 and r[3] > 0 for r in rows()), 30)
    except RuntimeError:
        return False, "the client's scoreboard rows: %s (it has %d raids itself)" % (rows(), client.call("GET", "/api/raids")["count"])
    return True, "client shows %s" % rows()


def check_weather(host, client):
    host.call("POST", "/api/weather", state="rain", hours=5)
    try:
        client.wait_for("the host's rain", lambda: client.call("GET", "/api/weather").get("state") == "Rain", 15)
    except RuntimeError:
        return False, "the client's weather: %s" % client.call("GET", "/api/weather")
    answer = client.call("GET", "/api/weather")
    if not answer.get("grassBoost", 1) > 1:
        return False, "no grass boost on the client: %s" % answer
    host.call("POST", "/api/weather", state="none")
    try:
        client.wait_for("the rain to end", lambda: client.call("GET", "/api/weather").get("state") == "None", 15)
    except RuntimeError:
        return False, "the client still has: %s" % client.call("GET", "/api/weather")
    return True, "client had the host's rain (boost %s) and its end" % answer["grassBoost"]


def ground_spots(host, offsets):
    """The empty voxels on the ground at the offsets from the home crystal (x, y, z)."""
    home = host.call("GET", "/api/ground?radius=0&heights=false")["center"]
    for dx, dz in offsets:
        ground = host.call("GET", "/api/ground?radius=0&heights=false&x=%d&z=%d" % (home[0] + dx, home[1] + dz))
        if ground.get("min") is None or ground["min"] < 0:
            continue
        yield (home[0] + dx, ground["min"] + 1, home[1] + dz)


def near(entries, at, distance=3):
    return [e for e in entries or [] if abs(e["position"][0] - at[0]) <= distance and abs(e["position"][2] - at[2]) <= distance]


def demolition_marks(player, own=False):
    return player.call("GET", "/api/demolish/marks", own="true" if own else "false")


def own_pallet(player, host, offsets, instant):
    """Places a stockpile of the player near the home crystal and has it built at once; its position."""
    for at in ground_spots(host, offsets):
        spot = "%d,%d,%d" % at
        if instant:
            answer = player.call("POST", "/api/blueprints/place", key="Pallet", at=spot, instant="true")
        else:
            answer = player.call("POST", "/api/blueprints/place", key="Pallet", at=spot)
        if not answer.get("placed"):
            continue
        if not instant:
            # The client's blueprint reaches the host a moment later; the host finishes it.
            host.wait_for("the client's stockpile blueprint", lambda: host.call("POST", "/api/blueprints/complete", everyone="true")["built"] > 0, 30)
        player.wait_for("its stockpile", lambda: len(near(demolition_marks(player, True)["own"], at)) > 0, 30)
        return near(demolition_marks(player, True)["own"], at)[0]["position"]
    raise RuntimeError("found no place for a stockpile round the home crystal")


def check_demolition(host, client):
    hosts = own_pallet(host, host, ((-9, 6), (9, 6), (-6, -9), (6, -9), (0, 12)), True)
    spot = "%g,%g,%g" % tuple(hosts)
    # Another player's building: the client sends the request (the API skips the client's own check), the host refuses.
    client.call("POST", "/api/demolish/mark", at=spot)
    time.sleep(3)
    if near(demolition_marks(host)["marks"], hosts):
        return False, "the host marked its own stockpile for the client's request"
    host.call("POST", "/api/demolish/mark", at=spot)
    try:
        client.wait_for("the host's demolition blueprint", lambda: len(near(demolition_marks(client)["marks"], hosts)) > 0, 15)
    except RuntimeError:
        return False, "the host's demolition blueprint did not reach the client: %s" % demolition_marks(client)
    host.call("POST", "/api/demolish/mark", at=spot, mark="false")
    client.wait_for("the host's cancel", lambda: not near(demolition_marks(client)["marks"], hosts), 15)
    clients = own_pallet(client, host, ((-12, -4), (12, -4), (-4, 15), (4, -15), (15, 4)), False)
    spot = "%g,%g,%g" % tuple(clients)
    client.call("POST", "/api/demolish/mark", at=spot)
    try:
        host.wait_for("the client's demolition in the task", lambda: any(m["task"] for m in near(demolition_marks(host)["marks"], clients)), 15)
        client.wait_for("its demolition blueprint", lambda: len(near(demolition_marks(client)["marks"], clients)) > 0, 15)
    except RuntimeError:
        return False, "the client's own demolition: host %s, client %s" % (demolition_marks(host), demolition_marks(client))
    client.call("POST", "/api/demolish/mark", at=spot, mark="false")
    try:
        host.wait_for("the client's cancel", lambda: not near(demolition_marks(host)["marks"], clients), 15)
    except RuntimeError:
        return False, "the client's cancel did not reach the host: %s" % demolition_marks(host)
    return True, "host refused the client's demolition of its stockpile; host's and client's demolitions and cancels reached both"


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
    # Since #127 a task starts with the host's "Max bricktrons per task" as its crew size, so free workers fill it.
    if project["maxCrew"] != TASK_CREW:
        return False, "the client's new task starts at %d, not the host's %d" % (project["maxCrew"], TASK_CREW)
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


# Checks that read what is drawn (a screenshot, a camera, a light): left out of a --headless run.
NEEDS_GRAPHICS = set()
VERSUS = "conquest_versus_f283061f-051c-4e70-8dd7-0edcf13bd3be"

EXTRA = []

MAPS = ["Apex", "Arecibo", "BigIsland", "Colibri", "Exile", "Helix", "Invictus", "Kairos", "Komorebi", "Nova", "Pineburg", "PlateauMont-Royal", "Quasar", "Riverains", "SteppingStones", "Tortuga", "TroubledHeaps", "ViperPass", "Zuma"]


def lobby_players(player):
    return player.call("GET", "/api/net").get("players", [])


def leave_to_menu(players):
    # The clients first, so the host's game is not what they see ending.
    for player in reversed(players):
        try:
            player.call("POST", "/api/net/leave")
        except Exception as e:
            log("[%s]" % player.name, "leave", repr(e))
    for player in reversed(players):
        player.wait_for("the title screen", lambda p=player: p.call("GET", "/api/status").get("scene") == "SceneMenu", 120)
    time.sleep(8)


def open_lobby(players, setup):
    host, clients = players[0], players[1:]
    host.call("POST", "/api/net/host", port=GAME_PORT, name="Host")
    host.wait_for("the lobby", lambda: any(p.get("local") for p in lobby_players(host)), 120)
    setup(host)
    for index, client in enumerate(clients):
        client.call("POST", "/api/net/join", address="127.0.0.1", port=GAME_PORT, name=["Client", "Client2"][index])
        want = index + 2
        host.wait_for("%s in the lobby" % client.name, lambda: len(lobby_players(host)) >= want, 120)
        time.sleep(3)


def start_from_lobby(players, loaded=False):
    host, clients = players[0], players[1:]
    for client in clients:
        client.call("POST", "/api/net/ready", value="true")
    time.sleep(2)
    log("lobby before the start:", lobby_players(host))
    host.call("POST", "/api/net/start", button="true")
    # A new game opens an introduction; a loaded save goes straight into the game.
    def started(p):
        if p.call("GET", "/api/dialog").get("open"):
            return True
        status = p.call("GET", "/api/status")
        return loaded and status.get("inGame") and not status.get("loading")

    for player in players:
        player.wait_for("the game's introduction" if not loaded else "the loaded game", lambda p=player: started(p), 600, 2)
    for player in players:
        if player.call("GET", "/api/dialog").get("open"):
            player.call("POST", "/api/dialog/press?index=0")
    time.sleep(5)


def alliance_groups(host):
    # The players' factions as groups of allies: [["Host", "client"], ["client2"]] by owner name.
    factions = host.call("GET", "/api/net/alliances")["factions"]
    groups = []
    for faction in factions:
        mine = sorted(faction["owners"])
        for group in groups:
            if mine[0] in group:
                break
        else:
            members = set(mine)
            for relation in faction["relations"]:
                if relation["allied"]:
                    other = [f for f in factions if f["faction"] == relation["faction"]][0]
                    members.update(other["owners"])
            groups.append(sorted(members))
    return sorted(groups)


def teams(player):
    return dict((p["name"], p["team"]) for p in lobby_players(player))


def check_saved_teams(host, client, in_game):
    players = [host, client]
    # Versus needs two teams: with only two players they could not share one, so a third plays too.
    third = Player(host.args, "Client2", host.port + 2, time.strftime("%Y-%m-%d-%H%M%S") + "-c2")
    EXTRA.append(third)
    third.start()
    players.append(third)
    if in_game:
        leave_to_menu([host, client])
    time.sleep(2)
    # Host and Client in one team, Client2 in the other.
    wanted = {"Host": 0, "Client": 0, "Client2": 1}

    def setup(h):
        # A map with a place for three players.
        for map_name in MAPS:
            try:
                answer = h.call("POST", "/api/net/mode", gamemode="conquest", preset=VERSUS, map=map_name)
            except RuntimeError:
                continue
            time.sleep(1)
            if h.call("GET", "/api/net").get("maxFactions", 0) >= 3:
                log("lobby:", answer)
                return
        raise RuntimeError("no Conquest map with a place for three players")

    open_lobby(players, setup)
    for name, team in wanted.items():
        host.call("POST", "/api/net/team", value=team, name=name)
    time.sleep(2)
    before = teams(host)
    if before != wanted:
        return False, "the lobby would not take the teams %s: %s" % (wanted, before)
    start_from_lobby(players)
    groups = alliance_groups(host)
    log("alliances:", host.call("GET", "/api/net/alliances"))
    if groups != [["Client", "Host"], ["Client2"]]:
        return False, "not as the lobby's teams before saving: allied groups %s" % groups
    saved = host.call("POST", "/api/net/save")
    log("saved as", saved["name"])
    leave_to_menu(players)
    # The players find the save in their own saves, as friends who play on one machine's saves do (the file times are
    # kept, the game matches on them); --no-save-copy leaves it to the game to send the save to the clients.
    for other in () if host.args.no_save_copy else players[1:]:
        shutil.copytree(os.path.join(host.root, "Info", "Saves", saved["name"]), os.path.join(other.root, "Info", "Saves", saved["name"]))
    open_lobby(players, lambda h: log("lobby:", h.call("POST", "/api/net/mode", save=saved["name"])))
    time.sleep(3)
    log("lobby of the loaded save:", lobby_players(host))
    after = teams(host)
    seen = teams(client)
    if after["Host"] != after["Client"] or after["Host"] == after["Client2"]:
        return False, "the loaded save's lobby changed the teams: %s (saved %s)" % (after, before)
    if seen != after:
        return False, "the client sees the teams %s, the host %s" % (seen, after)
    start_from_lobby(players, loaded=True)
    groups = alliance_groups(host)
    if groups != [["Client", "Host"], ["Client2"]]:
        return False, "teams %s in the lobby, but allied groups %s in the loaded game" % (after, groups)
    return True, "teams %s before saving, %s after loading; allied groups %s" % (before, after, groups)


CHECKS = [
    ("BricktronCap", check_bricktron_cap),
    ("ArmySelection", check_army_selection),
    ("BlueprintDeliveries", check_blueprint_deliveries),
    ("WaveRows", check_wave_rows),
    ("Weather", check_weather),
    ("TaskCrew", check_task_crew),
    ("Demolition", check_demolition),
    ("SavedTeams", check_saved_teams),
]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--filter", default="", help="part of a check's name (empty: all)")
    parser.add_argument("--port", type=int, default=27890, help="API port of the host; the client's is the next one (never 27860)")
    parser.add_argument("--player", default=os.path.join(REPO, "CastleStoryPlus.Player", "Build", "Linux"))
    parser.add_argument("--root-src", default=os.path.expanduser("~/.cache/csp-bench/root-src"))
    parser.add_argument("--work", default=os.path.expanduser("~/.cache/csp-tests"), help="folder for the copies, logs and results")
    parser.add_argument("--keep", action="store_true", help="keep the game root copies afterwards")
    parser.add_argument("--headless", action="store_true", help="start the players with -batchmode -nographics (no windows); checks that need rendering are left out")
    parser.add_argument("--no-save-copy", action="store_true", help="SavedTeams: do not copy the save to the clients, so the game has to send it (the download path)")
    parser.add_argument("--hold", type=int, default=0, help="keep the players running this many seconds after the checks")
    args = parser.parse_args()
    if not os.path.isfile(os.path.join(args.player, "run_bepinex.sh")):
        sys.exit("no run_bepinex.sh in " + args.player)
    if args.port <= USER_PORT <= args.port + 2:
        sys.exit("port %d is the player's own game: choose another --port" % USER_PORT)
    checks = [(name, check) for name, check in CHECKS if args.filter.lower() in name.lower()]
    if args.headless:
        for name in sorted(name for name, check in checks if name in NEEDS_GRAPHICS):
            log("headless: %s needs rendering and is left out" % name)
        checks = [(name, check) for name, check in checks if name not in NEEDS_GRAPHICS]
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
        in_game = False
        if any(name != "SavedTeams" for name, _ in checks):
            join(host, client)
            in_game = True
            log("in the game")
            time.sleep(5)
        for name, check in checks:
            begun = time.time()
            try:
                if name == "SavedTeams":
                    passed, message = check(host, client, in_game)
                    in_game = True
                else:
                    passed, message = check(host, client)
            except Exception as e:
                passed, message = False, "error: %s" % e
                # A check of a feature this edition leaves out (Essentials): its endpoint, feature or blueprint is
                # missing.
                if any(m in str(e) for m in ("No endpoint", "feature is off", "No blueprint 'Warehouse'")):
                    passed = None
            outcome = "Skipped" if passed is None else ("Passed" if passed else "Failed")
            results.append({"name": name, "outcome": outcome, "message": message, "seconds": round(time.time() - begun, 1)})
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
        for extra in EXTRA:
            extra.quit()
        client.quit()
        host.quit()
    path = os.path.join(args.work, "multiplayer-tests-%s.json" % stamp)
    with open(path, "w") as f:
        json.dump({"results": results}, f, indent=1)
    failed = [r for r in results if r["outcome"] == "Failed"]
    skipped = [r for r in results if r["outcome"] == "Skipped"]
    log("%d passed, %d failed, %d skipped; %s" % (len(results) - len(failed) - len(skipped), len(failed), len(skipped), path))
    sys.exit(1 if failed or not results else 0)


if __name__ == "__main__":
    main()
