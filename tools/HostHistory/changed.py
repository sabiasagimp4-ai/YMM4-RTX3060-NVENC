"""Temporary (host-history.yml): the top-level types whose code differs between two YMM4 versions, limited to what
the cache can depend on (rendering, the project model, the key, decoders, tachie; not the editor UI or voice engines).

  changed.py <newer report.tsv> <older report.tsv> <newer contracts.txt> <older contracts.txt>

Prints "Assembly<TAB>Type<TAB>changed|newer-only|older-only" (HostFingerprint report: "Assembly<TAB>Type<TAB>hash").
"""
import re
import sys

ASSEMBLIES = re.compile(r'^(YukkuriMovieMaker|YukkuriMovieMaker\.Plugin|YukkuriMovieMaker\.Settings'
                        r'|YukkuriMovieMaker\.Plugin\.Tachie\..+|YukkuriMovieMaker\.Plugin\.FileSource\..+|PsdParser)$')
UI = re.compile(r'(^|\.)(ViewModels|Views|Controls|Commands|Behaviors|Converters|Dialogs|Windows|Themes|Properties'
                r'|Resources|Update|Updater|XamlGeneratedNamespace)(\.|$)|^<|^__')


def report(path):
    types = {}
    for line in open(path, encoding='utf-8'):
        if line.startswith('#') or not line.strip():
            continue
        assembly, name, digest = line.rstrip('\n').split('\t')
        types[(assembly, name)] = digest
    return types


def contract_types(path):
    parts = set()
    for line in open(path, encoding='utf-8'):
        match = re.match(r'^  ([^|]+)\|(\S+) ', line)
        if match:
            parts.add((match.group(1), re.sub(r'::.*$', '', match.group(2)).rstrip('+*')))
    return parts


def top(name):
    return name.split('+', 1)[0]


def main(newer_report, older_report, newer_contracts, older_contracts):
    newer, older = report(newer_report), report(older_report)
    contracts = {(a, top(t)) for a, t in contract_types(newer_contracts) | contract_types(older_contracts)}
    status = {}
    for key in newer.keys() | older.keys():
        assembly, name = key
        if not ASSEMBLIES.match(assembly):
            continue
        owner = (assembly, top(name))
        if UI.search(owner[1]) and owner not in contracts:
            continue
        if newer.get(key) == older.get(key):
            continue
        # Any difference inside a top-level type (its nested types included) is read as a change of that type.
        status[owner] = 'changed' if owner in newer and owner in older else 'newer-only' if owner in newer else 'older-only'
    for (assembly, name), kind in sorted(status.items()):
        print(f'{assembly}\t{name}\t{kind}')


if __name__ == '__main__':
    main(*sys.argv[1:5])
