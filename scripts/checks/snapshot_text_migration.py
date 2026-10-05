"""Strict P6-26 text-only comparison. Structure, types and non-prose values stay frozen."""
from collections import Counter


def differences(old, new, allowed, path=()):
    counts, problems = Counter(), []
    if type(old) is not type(new):
        return counts, [f'{path}: type changed']
    if isinstance(old, dict):
        if old.keys() != new.keys():
            problems.append(f'{path}: keys changed')
        for key in old.keys() & new.keys():
            c, p = differences(old[key], new[key], allowed, path + (key,))
            counts.update(c)
            problems.extend(p)
    elif isinstance(old, list):
        if len(old) != len(new):
            problems.append(f'{path}: array length changed')
        for index, (a, b) in enumerate(zip(old, new)):
            c, p = differences(a, b, allowed, path + (index,))
            counts.update(c)
            problems.extend(p)
    elif old != new:
        category = allowed(path) if isinstance(old, str) else None
        if category:
            counts[category] += 1
        else:
            problems.append(f'{path}: non-text contract value changed')
    return counts, problems


def contract_text(path):
    if path and path[-1] == 'descriptionSha256':
        return 'tool-description'
    if path and path[-1] == 'description' and 'inputSchema' in path:
        return 'parameter-description'
    return None


def response_text(tool, path):
    # Explicit prose fields only. Never whitelist data, codes, outcome, enum,
    # execution, completeness, schema or an entire catalog response.
    if len(path) >= 2 and path[-2:] == ('error', 'message'):
        return 'error-message'
    if path and path[-1] == 'message' and 'warnings' in path and 'meta' in path:
        return 'warning-message'
    if path and path[-1] == 'description':
        return 'description'
    if tool == 'FindTools' and len(path) >= 3 and path[-3:-1] == ('data', 'items') and isinstance(path[-1], int):
        return 'discovery-description'
    return None
