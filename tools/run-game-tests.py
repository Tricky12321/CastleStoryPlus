#!/usr/bin/env python3
"""Runs the in-game tests (CastleStoryPlus.DevTools/Tests, [GameTest]) in a test player of their own: a fresh copy of
a game root (never the real saves), a new sandbox world on a map, the tests through the DevTools API, then the player
saves and closes and the copy is removed.

    tools/run-game-tests.py [--filter Combat] [--map Tortuga] [--speed 4] [--parallel 4] [--keep-units] [--player DIR]
                            [--root-src DIR] [--keep]

--player is a player folder with run_bepinex.sh and the mod (default: the Unity 6 port's
CastleStoryPlus.Player/Build/Linux). --root-src is a folder holding the game's Info (maps, Lua), copied for the run
(default ~/.cache/csp-bench/root-src). The results are printed and written to game-tests-<yyyy-MM-dd-HHmmss>.json
next to the player's log in the copy's parent folder (--out changes it). The exit code is 0 when no test failed.
No other game may answer on the API port (27860) while it runs.
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
API = "http://127.0.0.1:27860"


def log(*parts):
    print(time.strftime("%H:%M:%S"), *parts, flush=True)


def call(method, path, timeout=30):
    request = urllib.request.Request(API + path, method=method, data=b"" if method == "POST" else None)
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.load(response)


def answers():
    try:
        call("GET", "/api/status", 2)
        return True
    except Exception:
        return False


def wait_for(what, check, timeout, player):
    end = time.time() + timeout
    while time.time() < end:
        if player.poll() is not None:
            raise RuntimeError("the player exited while waiting for " + what)
        try:
            if check():
                return
        except (urllib.error.URLError, OSError, ValueError):
            pass
        time.sleep(1)
    raise RuntimeError("waited %d s for %s" % (timeout, what))


def start_world(args, player):
    query = urllib.parse.urlencode({"map": args.map, "gamemode": "sandbox"})
    log("new world", call("POST", "/api/world/new?" + query))

    def loaded():
        status = call("GET", "/api/status", 5)
        return status.get("inGame") and not status.get("loading") and status.get("worldStart") is None

    wait_for("the world to load", loaded, 600, player)
    # The game mode's introduction waits for a click before the world runs.
    for _ in range(10):
        dialog = call("GET", "/api/dialog")
        if not dialog.get("open"):
            break
        if not dialog.get("buttons"):
            break
        call("POST", "/api/dialog/press?button=0")
        time.sleep(2)


def settle(args, player):
    # A new world answers as loaded before its terrain and route graph are all in; the tests look for open ground.
    start = time.time()
    time.sleep(args.settle)

    def ground():
        found = call("GET", "/api/ground?radius=20&flat=6&heights=false", 30)
        return len(found.get("flat") or []) > 0

    wait_for("open ground near the home crystal", ground, 300, player)
    log("settled after %d s" % (time.time() - start))


def run_tests(args, player):
    query = {"speed": args.speed, "parallel": args.parallel, "clearUnits": "false" if args.keep_units else "true"}
    if args.filter:
        query["filter"] = args.filter
    log("run", call("POST", "/api/tests/run?" + urllib.parse.urlencode(query)))
    # A test skipped for want of free ground runs again later in a parallel run: shown again when its outcome changes.
    shown = {}
    end = time.time() + args.timeout
    while True:
        if player.poll() is not None:
            raise RuntimeError("the player exited during the tests")
        results = call("GET", "/api/tests/results?log=true")
        for result in results["results"]:
            if result["outcome"] not in ("Pending", "Running") and shown.get(result["name"]) != result["outcome"]:
                shown[result["name"]] = result["outcome"]
                line = "%-8s %-55s %6.1f s" % (result["outcome"], result["name"], result.get("seconds") or 0)
                log(line + (("  " + result["message"].split("\n")[0]) if result.get("message") else ""))
        if not results["running"]:
            return results
        if time.time() > end:
            call("POST", "/api/tests/stop")
            raise RuntimeError("the tests took over %d s" % args.timeout)
        time.sleep(2)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--filter", default="", help="part of a test name or category (empty: all)")
    parser.add_argument("--map", default="Tortuga", help="map of the sandbox world (trees, open ground)")
    parser.add_argument("--speed", type=float, default=4.0, help="game speed during the tests (0.25-20)")
    parser.add_argument("--player", default=os.path.join(REPO, "CastleStoryPlus.Player", "Build", "Linux"))
    parser.add_argument("--root-src", default=os.path.expanduser("~/.cache/csp-bench/root-src"))
    parser.add_argument("--work", default=os.path.expanduser("~/.cache/csp-tests"), help="folder for the copy, log and results")
    parser.add_argument("--timeout", type=int, default=3600, help="longest the whole run may take, in seconds")
    parser.add_argument("--keep", action="store_true", help="keep the game root copy afterwards")
    parser.add_argument("--parallel", type=int, default=4, help="tests at the same time (1-8): those of [ParallelTests] classes, one per group; the others alone")
    parser.add_argument("--keep-units", action="store_true", help="leave the world's own units (the player's bricktrons, roaming enemies) in it; by default they are taken away first")
    parser.add_argument("--hold", type=int, default=0, help="keep the player running this many seconds after the tests, to look into it")
    parser.add_argument("--settle", type=int, default=60, help="game seconds to let the new world settle (terrain, route graph) before the tests")
    parser.add_argument("--trace", action="store_true", help="switch the worker trace on for the run (BepInEx/workertrace.log in the player)")
    parser.add_argument("--vulkan", action="store_true", help="run the player on Vulkan instead of OpenGL Core")
    args = parser.parse_args()

    if not os.path.isfile(os.path.join(args.player, "run_bepinex.sh")):
        sys.exit("no run_bepinex.sh in " + args.player)
    if not os.path.isdir(os.path.join(args.root_src, "Info", "Maps")):
        sys.exit("no Info/Maps in " + args.root_src)
    if answers():
        sys.exit("another game answers on the API port; close it first")
    stamp = time.strftime("%Y-%m-%d-%H%M%S")
    os.makedirs(args.work, exist_ok=True)
    root = os.path.join(args.work, "root-" + stamp)
    shutil.copytree(os.path.join(args.root_src), root, symlinks=True)
    # The copy starts without saves: the tests change the world they run in.
    shutil.rmtree(os.path.join(root, "Info", "Saves"), ignore_errors=True)
    os.makedirs(os.path.join(root, "Info", "Saves"), exist_ok=True)
    player_log = os.path.join(args.work, "game-tests-%s.log" % stamp)
    env = dict(os.environ, CSP_GAME_ROOT=root)
    command = ["./run_bepinex.sh", "-force-vulkan" if args.vulkan else "-force-glcore", "-logFile", player_log]
    player = subprocess.Popen(command, cwd=args.player, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
    log("player", player.pid, "root", root)
    results = None
    error = None
    try:
        wait_for("the DevTools API", answers, 300, player)
        start_world(args, player)
        settle(args, player)
        if args.trace:
            call("POST", "/api/workertrace?on=true")
        results = run_tests(args, player)
        if args.hold > 0:
            log("holding the player for %d s" % args.hold)
            time.sleep(args.hold)
    except Exception as e:
        error = repr(e)
        log("error", error)
    finally:
        try:
            call("POST", "/api/quit?save=true", 120)
        except Exception as e:
            log("quit", repr(e))
        for _ in range(120):
            if player.poll() is not None:
                break
            time.sleep(1)
        if player.poll() is None:
            os.killpg(player.pid, signal.SIGTERM)
            time.sleep(10)
        if player.poll() is None:
            os.killpg(player.pid, signal.SIGKILL)
        if not args.keep:
            shutil.rmtree(root, ignore_errors=True)
    out = os.path.join(args.work, "game-tests-%s.json" % stamp)
    with open(out, "w") as f:
        json.dump({"error": error, "results": results, "log": player_log}, f, indent=1)
    totals = (results or {}).get("totals", {})
    log("totals", json.dumps(totals), "results", out)
    failed = error is not None or totals.get("Failed", 0) > 0 or totals.get("Error", 0) > 0
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
