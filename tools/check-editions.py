#!/usr/bin/env python3
"""Checks that the Essentials edition (docs/Editions.md) leaves out what it should, without the game's assemblies.

- every feature in CastleStoryPlus/Core/Features.cs has a row (In or Out) in the table of docs/Editions.md, and the
  table names no feature that does not exist;
- every [Feature(Features.X ...)] class of an Out feature is left out of the Essentials build: its file is removed in
  Editions.targets, or the attribute is inside #if !ESSENTIALS (or the #else of #if ESSENTIALS);
- every In feature still has a [Feature] class in the Essentials build.

Runs in CI (.github/workflows/editions.yml); the full compile of both editions needs the game and runs locally:
  dotnet build CastleStoryPlus -p:UnityTarget=player -p:Edition=Essentials
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MOD = os.path.join(ROOT, 'CastleStoryPlus')


def features():
    text = open(os.path.join(MOD, 'Core', 'Features.cs'), encoding='utf-8').read()
    return [n for n in re.findall(r'public const string (\w+) = "', text) if not n.endswith('Info')]


def table():
    rows = {}
    for line in open(os.path.join(ROOT, 'docs', 'Editions.md'), encoding='utf-8'):
        m = re.match(r'\|\s*`(\w+)`\s*\|\s*(In|Out)\b', line)
        if m:
            if m.group(1) in rows:
                fail('docs/Editions.md lists ' + m.group(1) + ' twice')
            rows[m.group(1)] = m.group(2)
    return rows


def removed_patterns():
    """The Compile Remove patterns of the mod project's Essentials item groups (paths relative to CastleStoryPlus/)."""
    text = open(os.path.join(ROOT, 'Editions.targets'), encoding='utf-8').read()
    patterns = []
    for group in re.findall(r'<ItemGroup Condition="([^"]*)">(.*?)</ItemGroup>', text, re.S):
        condition, body = group
        if "== 'CastleStoryPlus'" not in condition or "'Essentials'" not in condition:
            continue
        for include in re.findall(r'<Compile Remove="([^"]*)"', body):
            patterns += [p.strip() for p in include.split(';') if p.strip()]
    return patterns


def is_removed(relative, patterns):
    for pattern in patterns:
        regex = re.escape(pattern).replace(r'\*\*', '.*').replace(r'\*', '[^/]*')
        if re.fullmatch(regex, relative):
            return True
    return False


def essentials_lines(text):
    """For each line, whether it is compiled with ESSENTIALS defined (only #if/#else/#endif on ESSENTIALS and
    !ESSENTIALS are followed; other conditions count as compiled)."""
    compiled = []
    stack = []
    for line in text.split('\n'):
        stripped = line.strip()
        if stripped.startswith('#if'):
            cond = stripped[3:].strip()
            if cond == '!ESSENTIALS':
                stack.append(False)
            elif cond == 'ESSENTIALS':
                stack.append(True)
            else:
                stack.append(None)
        elif stripped.startswith('#else') and stack:
            if stack[-1] is not None:
                stack[-1] = not stack[-1]
        elif stripped.startswith('#endif') and stack:
            stack.pop()
        compiled.append(all(s is not False for s in stack))
    return compiled


FAILURES = []


def fail(message):
    FAILURES.append(message)


def main():
    names = features()
    rows = table()
    for name in names:
        if name not in rows:
            fail('Feature ' + name + ' has no row in docs/Editions.md (In or Out)')
    for name in rows:
        if name not in names:
            fail('docs/Editions.md lists ' + name + ', which is not in Features.cs')
    patterns = removed_patterns()
    kept = set()
    attribute = re.compile(r'\[Feature\(Features\.(\w+)')
    for folder, _, files in os.walk(MOD):
        if os.sep + 'obj' in folder or os.sep + 'bin' in folder:
            continue
        for f in files:
            if not f.endswith('.cs'):
                continue
            path = os.path.join(folder, f)
            relative = os.path.relpath(path, MOD).replace(os.sep, '/')
            removed = is_removed(relative, patterns)
            text = open(path, encoding='utf-8').read()
            compiled = essentials_lines(text)
            for number, line in enumerate(text.split('\n')):
                for name in attribute.findall(line):
                    in_essentials = not removed and compiled[number]
                    if rows.get(name) == 'Out' and in_essentials:
                        fail('CastleStoryPlus/' + relative + ':' + str(number + 1) + ': [Feature(' + name + ')] is Out but compiled into Essentials (remove the file in Editions.targets or put it under #if !ESSENTIALS)')
                    if in_essentials:
                        kept.add(name)
    for name, edition in rows.items():
        if edition == 'In' and name in names and name not in kept:
            fail('Feature ' + name + ' is In, but none of its [Feature] classes is compiled into Essentials')
    if FAILURES:
        for message in FAILURES:
            print('error: ' + message, file=sys.stderr)
        return 1
    print('Editions: ' + str(sum(1 for v in rows.values() if v == 'In')) + ' features In, ' + str(sum(1 for v in rows.values() if v == 'Out')) + ' Out; the Essentials build leaves out every Out feature')
    return 0


if __name__ == '__main__':
    sys.exit(main())
