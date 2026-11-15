#!/usr/bin/env python3
"""Runs the in-game tests (CastleStoryPlus.DevTools/Tests, [GameTest]) in test players of their own: each a fresh
copy of a game root (never the real saves), a new sandbox world on a map, the tests through the DevTools API, then
the player saves and closes and the copy is removed.

    tools/run-game-tests.py [--filter Combat] [--workers N] [--speed 10] [--parallel 4] [--map "Test Island"]
                            [--keep-units] [--player DIR] [--root-src DIR] [--keep] [--lock FILE]

--workers starts that many players at once (default: from the CPU cores and the free memory), each on its own copy of
the game root and its own API port (--port, 27870 and up; never 27860, the port of the player's own game). The tests
are sorted by name and dealt out to the players in turn, and their results merged into one file. Each test works on
its own plot of ground, so the players need nothing from each other.

The default map is Test Island: round, perfectly flat and empty (tools/make_test_island.py makes it in the copy, never
in the game's own folder), so every test finds the same ground on its own plot in every run and the results do not
depend on what ran before or next to it. The tests that need trees plant their own.

--speed is the game speed during the tests (default 10). Tests marked [GameTest(MaxSpeed = ...)] run alone at that
speed at most.

--player is a player folder with run_bepinex.sh and the mod (default: the Unity 6 port's
CastleStoryPlus.Player/Build/Linux). --root-src is a folder holding the game's Info (maps, Lua), copied for the run
(default ~/.cache/csp-bench/root-src). The results are printed and written to game-tests-<yyyy-MM-dd-HHmmss>.json in
--work, next to the players' logs. The exit code is 0 when no test failed. --lock holds an exclusive lock on the file
for the whole run (players of other runs wait).
"""
import argparse
import fcntl
import json
import os
import shutil
import signal
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TEST_ISLAND = "Test Island"
# The player's own game answers on 27860: the test players stay off it.
USER_PORT = 27860
# Where the route graph must be in before the tests on Test Island: the home crystal and the far corners of the
# plots (Scenario.Plot: up to 360 voxels from it).
SETTLE_POINTS = [(0, 0), (330, 0), (-330, 0), (0, 330), (0, -330)]

_print_lock = threading.Lock()

_start_lock = threading.Lock()


def log(*parts):
    with _print_lock:
        print(time.strftime("%H:%M:%S"), *parts, flush=True)


def default_workers():
    cores = os.cpu_count() or 2
    free_gb = 8
    try:
        with open("/proc/meminfo") as f:
            for line in f:
                if line.startswith("MemAvailable:"):
                    free_gb = int(line.split()[1]) // (1024 * 1024)
    except OSError:
        pass
    # A player takes about 3.5 GB and keeps 2-3 cores busy (main thread, render, jobs). Measured on 16 cores and 32 GB
    # (15-11-2026, full run at 10x): 2 players 154 s, 3 players 165 s, 4 players 192 s, 6 players 246 s, 8 players
    # 299 s. The run is as long as its longest tests (about 100 s at 10x), and every player more starts later (they
    # start one after the other) and makes the others' frames slower.
    return max(1, min(2, cores // 4, int(free_gb // 4)))


class Player:
    def __init__(self, args, index, stamp):
        self.args = args
        self.index = index
        self.port = args.port + index
        self.api = "http://127.0.0.1:%d" % self.port
        self.name = "w%d" % index
        self.root = os.path.join(args.work, "root-%s-%s" % (stamp, self.name))
        self.log_file = os.path.join(args.work, "game-tests-%s-%s.log" % (stamp, self.name))
        self.process = None
        self.results = None
        self.error = None
        self.tests = []

    def say(self, *parts):
        log("[%s]" % self.name, *parts)

    def call(self, method, path, timeout=30):
        request = urllib.request.Request(self.api + path, method=method, data=b"" if method == "POST" else None)
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return json.load(response)

    def answers(self):
        try:
            self.call("GET", "/api/status", 2)
            return True
        except Exception:
            return False

    def wait_for(self, what, check, timeout, interval=0.5):
        end = time.time() + timeout
        while time.time() < end:
            if self.process is not None and self.process.poll() is not None:
                raise RuntimeError("the player exited while waiting for " + what)
            try:
                if check():
                    return
            except (urllib.error.URLError, OSError, ValueError):
                pass
            time.sleep(interval)
        raise RuntimeError("waited %d s for %s" % (timeout, what))

    def start(self):
        args = self.args
        if self.answers():
            raise RuntimeError("another game answers on port %d" % self.port)
        shutil.copytree(args.root_src, self.root, symlinks=True)
        # The copy starts without saves: the tests change the world they run in.
        shutil.rmtree(os.path.join(self.root, "Info", "Saves"), ignore_errors=True)
        os.makedirs(os.path.join(self.root, "Info", "Saves"), exist_ok=True)
        if args.map == TEST_ISLAND:
            # Into the copy only: remade for every run, so it always matches the plots the tests expect.
            script = os.path.join(REPO, "tools", "make_test_island.py")
            subprocess.run([sys.executable, script, "--game", self.root, "--radius", str(args.island_radius)], check=True, stdout=subprocess.DEVNULL)
        env = dict(os.environ, CSP_GAME_ROOT=self.root, CSP_API_PORT=str(self.port))
        command = ["./run_bepinex.sh", "-force-vulkan" if args.vulkan else "-force-glcore", "-logFile", self.log_file]
        started = time.time()
        for attempt in range(3):
            # The players share the player folder and its BepInEx config, which each one reads and writes back while
            # the mod loads: one after the other, until its API answers.
            with _start_lock:
                self.process = subprocess.Popen(command, cwd=args.player, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
                self.say("player", self.process.pid, "port", self.port, "root", self.root)
                try:
                    self.wait_for("the DevTools API", self.answers, 300)
                    break
                except RuntimeError as e:
                    if self.process.poll() is None or attempt == 2:
                        raise
                    # A player now and then dies while it starts (a native crash in the engine): once more.
                    self.say("the player exited while starting (%s), starting it again" % e)
                    os.rename(self.log_file, self.log_file + ".crash%d" % attempt)
        query = urllib.parse.urlencode({"map": args.map, "gamemode": "sandbox"})
        self.call("POST", "/api/world/new?" + query)

        def loaded():
            status = self.call("GET", "/api/status", 5)
            return status.get("inGame") and not status.get("loading") and status.get("worldStart") is None

        self.wait_for("the world to load", loaded, 600)
        # The game mode's introduction waits for a click before the world runs.
        for _ in range(10):
            dialog = self.call("GET", "/api/dialog")
            if not dialog.get("open") or not dialog.get("buttons"):
                break
            self.call("POST", "/api/dialog/press?button=0")
            time.sleep(0.5)
        self.settle()
        self.say("ready after %d s" % (time.time() - started))

    def settle(self):
        # A new world answers as loaded before its terrain and route graph are all in; the tests look for open ground
        # and stand their units on the route graph.
        if self.args.settle > 0:
            time.sleep(self.args.settle)

        def ground():
            found = self.call("GET", "/api/ground?radius=20&flat=6&heights=false", 30)
            return len(found.get("flat") or []) > 0

        self.wait_for("open ground near the home crystal", ground, 300)
        if self.args.map != TEST_ISLAND:
            return
        home = self.call("GET", "/api/ground?radius=0&heights=false", 30)["center"]

        def graph():
            for dx, dz in SETTLE_POINTS:
                answer = self.call("GET", "/api/path/ground?x=%d&z=%d" % (home[0] + dx, home[1] + dz), 10)
                if not answer.get("found"):
                    return False
            return True

        self.wait_for("the route graph over the island", graph, 300)

    def run_tests(self):
        args = self.args
        query = {"speed": args.speed, "parallel": args.parallel, "clearUnits": "false" if args.keep_units else "true", "names": ",".join(self.tests)}
        self.call("POST", "/api/tests/run?" + urllib.parse.urlencode(query))
        self.say("running", len(self.tests), "tests")
        # A test skipped for want of free ground runs again later in a parallel run: shown again when its outcome changes.
        shown = {}
        silent_since = None
        end = time.time() + args.timeout
        while True:
            if self.process.poll() is not None:
                raise RuntimeError("the player exited during the tests")
            try:
                results = self.call("GET", "/api/tests/results?log=true", 60)
                silent_since = None
            except (urllib.error.URLError, OSError, ValueError) as e:
                # A long frame (a big flood, a garbage collection) can keep the game from answering for a while.
                silent_since = silent_since or time.time()
                if time.time() - silent_since > 180:
                    raise RuntimeError("the player has not answered for 180 s: %r" % e)
                time.sleep(2)
                continue
            for result in results["results"]:
                if result["outcome"] not in ("Pending", "Running") and shown.get(result["name"]) != result["outcome"]:
                    shown[result["name"]] = result["outcome"]
                    line = "%-8s %-55s %6.1f s" % (result["outcome"], result["name"], result.get("seconds") or 0)
                    self.say(line + (("  " + result["message"].split("\n")[0]) if result.get("message") else ""))
            if not results["running"]:
                return results
            if time.time() > end:
                self.call("POST", "/api/tests/stop")
                raise RuntimeError("the tests took over %d s" % args.timeout)
            time.sleep(1)

    def quit(self):
        if self.process is None:
            return
        try:
            answer = self.call("POST", "/api/quit?save=true", 120)
            if not answer.get("quitting", True):
                self.say("quit:", answer)
        except Exception as e:
            self.say("quit", repr(e))
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


def merge(players):
    results = []
    totals = {}
    for player in players:
        if player.results:
            results.extend(player.results["results"])
    results.sort(key=lambda r: r["name"])
    for result in results:
        totals[result["outcome"]] = totals.get(result["outcome"], 0) + 1
    return {"run": 1, "running": False, "totals": totals, "results": results}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--filter", default="", help="part of a test name or category (empty: all)")
    parser.add_argument("--map", default=TEST_ISLAND, help="map of the sandbox world (default: Test Island, made in the copy)")
    parser.add_argument("--island-radius", type=int, default=400, help="radius of Test Island in voxels (room for every test's plot)")
    parser.add_argument("--speed", type=float, default=10.0, help="game speed during the tests (0.25-20, default 10)")
    parser.add_argument("--workers", type=int, default=0, help="players at the same time, each with its own share of the tests (default: from CPU cores and free memory)")
    parser.add_argument("--port", type=int, default=27870, help="API port of the first player; the others count up from it (never 27860)")
    parser.add_argument("--player", default=os.path.join(REPO, "CastleStoryPlus.Player", "Build", "Linux"))
    parser.add_argument("--root-src", default=os.path.expanduser("~/.cache/csp-bench/root-src"))
    parser.add_argument("--work", default=os.path.expanduser("~/.cache/csp-tests"), help="folder for the copies, logs and results")
    parser.add_argument("--timeout", type=int, default=3600, help="longest the tests may take in each player, in seconds")
    parser.add_argument("--keep", action="store_true", help="keep the game root copies afterwards")
    parser.add_argument("--parallel", type=int, default=4, help="tests at the same time in each player (1-8): those of [ParallelTests] classes, one per group; the others alone")
    parser.add_argument("--keep-units", action="store_true", help="leave the world's own units (the player's bricktrons, roaming enemies) in it; by default they are taken away first")
    parser.add_argument("--hold", type=int, default=0, help="keep the players running this many seconds after the tests, to look into them")
    parser.add_argument("--settle", type=int, default=0, help="extra seconds to let the new world settle before the tests (the runner waits for the route graph anyway)")
    parser.add_argument("--trace", action="store_true", help="switch the worker trace on for the run (BepInEx/workertrace.log in the player)")
    parser.add_argument("--vulkan", action="store_true", help="run the players on Vulkan instead of OpenGL Core")
    parser.add_argument("--lock", default="", help="file to hold an exclusive lock on for the whole run")
    args = parser.parse_args()

    if not os.path.isfile(os.path.join(args.player, "run_bepinex.sh")):
        sys.exit("no run_bepinex.sh in " + args.player)
    if not os.path.isdir(os.path.join(args.root_src, "Info", "Maps")):
        sys.exit("no Info/Maps in " + args.root_src)
    workers = args.workers if args.workers > 0 else default_workers()
    if args.port <= USER_PORT < args.port + workers:
        sys.exit("port %d is the player's own game: choose --port above it" % USER_PORT)
    lock = None
    if args.lock:
        os.makedirs(os.path.dirname(os.path.abspath(args.lock)), exist_ok=True)
        lock = open(args.lock, "w")
        fcntl.flock(lock, fcntl.LOCK_EX)
    begun = time.time()
    stamp = time.strftime("%Y-%m-%d-%H%M%S")
    os.makedirs(args.work, exist_ok=True)
    players = [Player(args, i, stamp) for i in range(workers)]
    log("%d player(s), speed %gx, parallel %d" % (workers, args.speed, args.parallel))
    error = None
    try:
        # All players start at once (copy, load, settle); the tests are dealt out once they are all in.
        threads = [threading.Thread(target=lambda p=p: _guard(p, p.start)) for p in players]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()
        failed = [p for p in players if p.error]
        if failed:
            raise RuntimeError("; ".join("%s: %s" % (p.name, p.error) for p in failed))
        ready = time.time()
        log("players ready after %d s" % (ready - begun))
        names = sorted(t["name"] for t in players[0].call("GET", "/api/tests?" + urllib.parse.urlencode({"filter": args.filter}))["tests"])
        if not names:
            raise RuntimeError("no test matches " + repr(args.filter))
        # Sorted by name and dealt out in turn: the same share for each player in every run.
        for i, name in enumerate(names):
            players[i % workers].tests.append(name)
        if args.trace:
            for player in players:
                player.call("POST", "/api/workertrace?on=true")
        active = [p for p in players if p.tests]
        threads = [threading.Thread(target=lambda p=p: _guard(p, lambda: setattr(p, "results", p.run_tests()))) for p in active]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()
        log("tests took %d s" % (time.time() - ready))
        failed = [p for p in active if p.error]
        if failed:
            error = "; ".join("%s: %s" % (p.name, p.error) for p in failed)
            log("error", error)
        if args.hold > 0:
            log("holding the players for %d s" % args.hold)
            time.sleep(args.hold)
    except Exception as e:
        error = repr(e)
        log("error", error)
    finally:
        threads = [threading.Thread(target=p.quit) for p in players]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()
    results = merge(players)
    wall = round(time.time() - begun)
    out = os.path.join(args.work, "game-tests-%s.json" % stamp)
    with open(out, "w") as f:
        json.dump({"error": error, "results": results, "log": players[0].log_file, "logs": [p.log_file for p in players], "workers": workers, "speed": args.speed, "parallel": args.parallel, "wallSeconds": wall}, f, indent=1)
    totals = results.get("totals", {})
    log("totals", json.dumps(totals), "wall %d s" % wall, "results", out)
    if lock is not None:
        lock.close()
    failed = error is not None or totals.get("Failed", 0) > 0 or totals.get("Error", 0) > 0
    sys.exit(1 if failed else 0)


def _guard(player, action):
    try:
        action()
    except Exception as e:
        player.error = repr(e)
        player.say("error", player.error)


if __name__ == "__main__":
    main()
