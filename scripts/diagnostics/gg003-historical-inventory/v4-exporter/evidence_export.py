"""Offline evidence writer. No SQL driver, credentials or network path.

An independently validated adapter must supply exact SQL schemas and typed rows.
This module does not attest database provenance or authorize SQL execution.
"""
import hashlib
import json
import os
import re
import shutil
import tempfile
import time
import uuid
from contextlib import contextmanager
from datetime import datetime
from decimal import Decimal
from pathlib import Path

SQL_SHA256 = 'dbb7af00aa97241f789d982b43a5740c774a79973360bcb6ba66d51d8cac7719'
SETS = ('Q2B-target', 'Q2B-cut', 'Q2B-contribution-boundary',
        'Q2B-fact-membership', 'Q2B-duration-operands', 'Q2B-coverage', 'Q2B-end-target')

class EvidenceError(ValueError):
    pass

def canonical(value):
    return (json.dumps(value, ensure_ascii=False, sort_keys=True,
                       separators=(',', ':'), allow_nan=False) + '\n').encode('utf-8')

def digest(data):
    return hashlib.sha256(data).hexdigest()

def cell(value, col):
    if value is None:
        if not col['nullable']:
            raise EvidenceError('NULL in nonnullable column')
        return None
    kind = col['sql_type']
    if kind in ('nvarchar', 'varchar', 'nchar', 'char'):
        if type(value) is not str:
            raise EvidenceError('Text must arrive as exact string')
        value.encode('utf-8', errors='strict')
        limit = col.get('max_code_units')
        if limit is not None and len(value.encode('utf-16-le')) // 2 > limit:
            raise EvidenceError('Text exceeds declared metadata')
        return value
    if kind in ('decimal', 'numeric'):
        if type(value) is not Decimal or not value.is_finite():
            raise EvidenceError('Decimal requires finite Decimal, never float')
        precision, scale = col['precision'], col['scale']
        if not (1 <= precision <= 38 and 0 <= scale <= precision):
            raise EvidenceError('Invalid SQL decimal metadata')
        # Preserve original decimal scale; no arithmetic/context rounding.
        text = format(value, 'f')
        integer, _, fraction = text.lstrip('-').partition('.')
        if len(fraction) > scale or len(integer.lstrip('0')) > precision - scale:
            raise EvidenceError('Decimal incompatible with declared precision/scale')
        return text
    if kind in ('bigint', 'int', 'smallint', 'tinyint'):
        bits = {'bigint': 64, 'int': 32, 'smallint': 16, 'tinyint': 8}[kind]
        low, high = (0, 255) if kind == 'tinyint' else (-(2**(bits-1)), 2**(bits-1)-1)
        if type(value) is not int or not low <= value <= high:
            raise EvidenceError('Integer type/range mismatch')
        return str(value)
    if kind == 'bit':
        if type(value) is not bool:
            raise EvidenceError('Bit requires bool')
        return value
    if kind in ('binary', 'varbinary'):
        if type(value) is not bytes:
            raise EvidenceError('Binary requires exact bytes')
        if col.get('max_bytes') is not None and len(value) > col['max_bytes']:
            raise EvidenceError('Binary exceeds metadata')
        return value.hex().upper()
    if kind == 'sql_variant':
        if (type(value) is not dict or set(value) != {'provider_type', 'value'}
                or value['provider_type'] != 'System.String' or type(value['value']) is not str):
            raise EvidenceError('Only exact string-valued SERVERPROPERTY variant supported')
        return value
    if kind == 'uniqueidentifier':
        if type(value) is not uuid.UUID:
            raise EvidenceError('GUID requires UUID')
        return str(value)
    if kind == 'date':
        if type(value) is not str or not re.fullmatch(r'\d{4}-\d{2}-\d{2}', value):
            raise EvidenceError('Exact ISO date string required')
        datetime.strptime(value, '%Y-%m-%d')
        return value
    if kind == 'datetimeoffset':
        scale = col['scale']
        if not 0 <= scale <= 7 or type(value) is not str:
            raise EvidenceError('Exact SQL timestamp string required')
        fraction = r'\.\d{' + str(scale) + '}' if scale else ''
        if not re.fullmatch(r'\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}' + fraction + r'[+-]\d{2}:\d{2}', value):
            raise EvidenceError('Timestamp lost scale or offset')
        datetime.fromisoformat(value)  # Validation only. Never reserialize microseconds.
        offset = value[-6:]
        h, m = int(offset[1:3]), int(offset[4:])
        if h > 14 or m > 59 or (h == 14 and m):
            raise EvidenceError('Invalid SQL offset')
        return value
    raise EvidenceError('Unsupported SQL type: ' + kind)

def export(reader, expected_schemas, sql_bytes, output, *, context,
           max_bytes=10*1024*1024, timeout_seconds=60, max_rows_per_set=1000,
           clock=time.monotonic, finalize=None):
    """reader yields (schema, row_iterator); schema order is authoritative.

    Deadline is cooperative. A future DB adapter MUST enforce command timeout
    and cancellation independently: this core cannot interrupt a blocked reader.
    Output is one atomic directory containing data.jsonl and manifest.json.
    """
    if digest(sql_bytes) != SQL_SHA256:
        raise EvidenceError('Frozen SQL SHA256 mismatch')
    if len(expected_schemas) != 7 or tuple(s['name'] for s in expected_schemas) != SETS:
        raise EvidenceError('Seven exact ordered schemas required')
    if max_bytes <= 0 or timeout_seconds <= 0 or max_rows_per_set <= 0:
        raise EvidenceError('Positive resource limits required')
    for schema in expected_schemas:
        cols = schema['columns']
        if not cols or [c['ordinal'] for c in cols] != list(range(len(cols))):
            raise EvidenceError('Column ordinal mismatch')
        if len({c['name'] for c in cols}) != len(cols):
            raise EvidenceError('Duplicate column name')
        if cols[0]['name'] != 'ResultSet':
            raise EvidenceError('ResultSet column required')
    output = Path(output)
    if output.exists():
        raise EvidenceError('Refuse to replace existing evidence')
    started = clock()
    def deadline():
        if clock() - started >= timeout_seconds:
            raise TimeoutError('Export deadline exceeded')
    temp = Path(tempfile.mkdtemp(prefix='.q2b-incomplete-', dir=output.parent))
    try:
        written, counts = 0, []
        with (temp / 'data.jsonl').open('wb') as stream:
            def emit(obj):
                nonlocal written
                deadline()
                data = canonical(obj)
                written += len(data)
                if written > max_bytes:
                    raise EvidenceError('Output byte limit exceeded')
                stream.write(data)
            for index, (schema, rows) in enumerate(reader):
                deadline()
                if index >= 7 or schema != expected_schemas[index]:
                    raise EvidenceError('Unexpected result schema/order')
                emit({'kind': 'schema', 'result_ordinal': index, 'schema': schema})
                count = 0
                for row in rows:
                    deadline()
                    if len(row) != len(schema['columns']):
                        raise EvidenceError('Row width mismatch/truncation')
                    if row[0] != schema['name']:
                        raise EvidenceError('ResultSet label mismatch')
                    count += 1
                    if count > max_rows_per_set:
                        raise EvidenceError('Row limit exceeded')
                    values = [cell(v, c) for v, c in zip(row, schema['columns'])]
                    emit({'kind': 'row', 'result_ordinal': index, 'row_ordinal': count-1, 'values': values})
                counts.append(count)
            deadline()
            if len(counts) != 7:
                raise EvidenceError('Missing result set')
            stream.flush()
            os.fsync(stream.fileno())
        # Live adapter's terminal-success/identity gate runs BEFORE publication.
        terminal_receipt = finalize(counts) if finalize is not None else None
        deadline()
        data = (temp / 'data.jsonl').read_bytes()
        manifest = {'format': 'q2b-evidence-core/v1', 'complete': True,
                    'scope': 'typed-reader-capture-only', 'terminal_receipt': terminal_receipt, 'sql_sha256': digest(sql_bytes),
                    'sql_bytes': len(sql_bytes), 'schemas': expected_schemas,
                    'schema_sha256': digest(canonical(expected_schemas)), 'row_counts': counts,
                    'data_bytes': len(data), 'data_sha256': digest(data), 'context': context,
                    'limits': {'max_total_bytes': max_bytes, 'cooperative_timeout_seconds': timeout_seconds,
                               'max_rows_per_set': max_rows_per_set}}
        manifest_bytes = canonical(manifest)
        if len(data) + len(manifest_bytes) > max_bytes:
            raise EvidenceError('Manifest plus data exceeds output byte limit')
        with (temp / 'manifest.json').open('wb') as stream:
            stream.write(manifest_bytes)
            stream.flush()
            os.fsync(stream.fileno())
        deadline()
        # Dedicated output path; concurrent publishers are outside this core contract.
        if output.exists():
            raise EvidenceError('Output already exists')
        with publication_lock(output):
            os.rename(temp, output)
        return {'directory': str(output), 'data_sha256': digest(data),
                'manifest_sha256': digest(manifest_bytes),
                'total_bytes': len(data)+len(manifest_bytes), 'row_counts': counts}
    except BaseException:
        shutil.rmtree(temp, ignore_errors=True)
        raise

@contextmanager
def publication_lock(output):
    """Cooperating publishers share an exclusive sibling lock; never steal stale locks."""
    output = Path(output)
    lock = output.parent / (output.name + '.publish.lock')
    fd = None
    try:
        fd = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        os.write(fd, str(os.getpid()).encode('ascii'))
        if output.exists():
            raise EvidenceError('Output already exists under publication lock')
        yield
    finally:
        if fd is not None:
            os.close(fd)
            lock.unlink()
