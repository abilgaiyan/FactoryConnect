import copy
import hashlib
import json
import tempfile
import unittest
import uuid
from decimal import Decimal
from pathlib import Path
from evidence_export import export, SETS, EvidenceError, SQL_SHA256

SQL = (Path(__file__).parent.parent / 'Q2B-revision-534-lineage-review.sql').read_bytes()

def fixture():
    columns = [
        {'ordinal': 0, 'name': 'ResultSet', 'sql_type': 'nvarchar', 'nullable': False},
        {'ordinal': 1, 'name': 'MetricValue', 'sql_type': 'nvarchar', 'nullable': False},
        {'ordinal': 2, 'name': 'ExactDecimal', 'sql_type': 'decimal', 'precision': 38, 'scale': 18, 'nullable': False},
        {'ordinal': 3, 'name': 'Position', 'sql_type': 'decimal', 'precision': 20, 'scale': 0, 'nullable': False},
        {'ordinal': 4, 'name': 'ObservedAt', 'sql_type': 'datetimeoffset', 'scale': 7, 'nullable': False},
        {'ordinal': 5, 'name': 'Nullable', 'sql_type': 'nvarchar', 'nullable': True},
        {'ordinal': 6, 'name': 'Empty', 'sql_type': 'nvarchar', 'nullable': False},
        {'ordinal': 7, 'name': 'Identity', 'sql_type': 'varbinary', 'nullable': False},
        {'ordinal': 8, 'name': 'Identifier', 'sql_type': 'uniqueidentifier', 'nullable': False},
        {'ordinal': 9, 'name': 'LargeInteger', 'sql_type': 'bigint', 'nullable': False},
    ]
    schemas = [{'name': name, 'columns': copy.deepcopy(columns)} for name in SETS]
    row = ['  2683.6141570000\t|"\r\nहिन्दी 😀 ', Decimal('12345678901234567890.123456789012345678'),
           Decimal('18446744073709551615'), '2026-10-16T06:40:52.0133557+05:30',
           None, '', b'\x00\xff\x10', uuid.UUID('677b030b-1ede-4a67-82ae-6a494791357f'), 9223372036854775807]
    sets = [(schema, [[schema['name']] + copy.deepcopy(row)]) for schema in schemas]
    # Repeated identical rows must survive. No deduplication.
    sets[3][1].append(copy.deepcopy(sets[3][1][0]))
    return schemas, sets

class ExportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.addCleanup(self.temp.cleanup)

    def run_export(self, schemas=None, sets=None, **options):
        if schemas is None:
            schemas, default = fixture()
            sets = default if sets is None else sets
        return export(iter(sets), schemas, SQL, self.root / 'evidence',
                      context={'fixture': True, 'source_commit': 'synthetic'}, **options)

    def assert_export_rejected(self, schemas, sets, exception=EvidenceError, **options):
        with self.assertRaises(exception):
            self.run_export(schemas, sets, **options)
        self.assertEqual([], list(self.root.iterdir()))

    def test_frozen_sql(self):
        self.assertEqual(SQL_SHA256, hashlib.sha256(SQL).hexdigest())

    def test_roundtrip_types_metadata_and_duplicate_rows(self):
        schemas, sets = fixture()
        result = self.run_export(schemas, sets)
        data = (self.root/'evidence/data.jsonl').read_bytes()
        manifest = json.loads((self.root/'evidence/manifest.json').read_bytes())
        self.assertEqual(schemas, manifest['schemas'])
        self.assertEqual([1,1,1,2,1,1,1], manifest['row_counts'])
        self.assertEqual(hashlib.sha256(data).hexdigest(), result['data_sha256'])
        rows = [r for r in map(json.loads, data.splitlines()) if r['kind']=='row']
        values = rows[0]['values']
        self.assertEqual(sets[0][1][0][1], values[1])
        self.assertEqual('12345678901234567890.123456789012345678', values[2])
        self.assertEqual('18446744073709551615', values[3])
        self.assertEqual(sets[0][1][0][4], values[4])
        self.assertIsNone(values[5]); self.assertEqual('', values[6])
        self.assertEqual('00FF10', values[7]); self.assertEqual('9223372036854775807', values[9])
        self.assertEqual(len(data), manifest['data_bytes'])

    def test_deterministic_bytes(self):
        self.run_export()
        original = (self.root/'evidence/data.jsonl').read_bytes(), (self.root/'evidence/manifest.json').read_bytes()
        import shutil
        shutil.rmtree(self.root/'evidence')
        self.run_export()
        self.assertEqual(original, ((self.root/'evidence/data.jsonl').read_bytes(), (self.root/'evidence/manifest.json').read_bytes()))

    def test_missing_and_extra_sets(self):
        for mode in ('missing','extra'):
            s,r=fixture(); r = r[:-1] if mode=='missing' else r+[r[-1]]
            self.assert_export_rejected(s,r)

    def test_schema_and_order(self):
        s,r=fixture(); r[0],r[1]=r[1],r[0]; self.assert_export_rejected(s,r)
        s,r=fixture(); r=copy.deepcopy(r); r[0][0]['columns'][1]['sql_type']='varchar'; self.assert_export_rejected(s,r)

    def test_row_truncation_and_label(self):
        s,r=fixture(); r[0][1][0].pop(); self.assert_export_rejected(s,r)
        s,r=fixture(); r[0][1][0][0]='wrong'; self.assert_export_rejected(s,r)

    def test_float_decimal_rejected(self):
        s,r=fixture(); r[0][1][0][2]=0.1; self.assert_export_rejected(s,r)

    def test_precision_overflow_and_nonfinite(self):
        for value in ('123456789012345678901.1', 'NaN', '1.1234567890123456789'):
            s,r=fixture(); r[0][1][0][2]=Decimal(value); self.assert_export_rejected(s,r)

    def test_timestamp_precision_loss_rejected(self):
        s,r=fixture(); r[0][1][0][4]='2026-10-16T06:40:52.013355+05:30'; self.assert_export_rejected(s,r)

    def test_nonnullable_null_and_invalid_unicode(self):
        s,r=fixture(); r[0][1][0][1]=None; self.assert_export_rejected(s,r)
        s,r=fixture(); r[0][1][0][1]='\ud800'; self.assert_export_rejected(s,r,UnicodeError)

    def test_size_and_row_limits(self):
        s,r=fixture(); self.assert_export_rejected(s,r,max_bytes=100)
        s,r=fixture(); self.assert_export_rejected(s,r,max_rows_per_set=1)

    def test_manifest_size_included_in_limit(self):
        self.run_export()
        data_size=(self.root/'evidence/data.jsonl').stat().st_size
        import shutil
        shutil.rmtree(self.root/'evidence')
        s,r=fixture(); self.assert_export_rejected(s,r,max_bytes=data_size+1)

    def test_midrow_reader_failure(self):
        s,r=fixture()
        first_row=r[0][1][0]
        def broken_rows():
            yield first_row
            raise RuntimeError('synthetic reader failure after a row')
        r[0]=(r[0][0],broken_rows())
        self.assert_export_rejected(s,r,RuntimeError)

    def test_deadline(self):
        s,r=fixture(); ticks=iter([0,61]); self.assert_export_rejected(s,r,TimeoutError,clock=lambda:next(ticks))

    def test_reader_errors_and_timeouts(self):
        for error in (RuntimeError('synthetic SQL error'), TimeoutError('synthetic command timeout')):
            s,r=fixture()
            def broken():
                yield r[0]
                raise error
            self.assert_export_rejected(s,broken(),type(error))

    def test_bad_sql_identity(self):
        s,r=fixture()
        with self.assertRaises(EvidenceError):
            export(r,s,SQL+b' ',self.root/'evidence',context={})
        self.assertEqual([],list(self.root.iterdir()))

    def test_existing_output_never_overwritten(self):
        self.run_export(); before=(self.root/'evidence/data.jsonl').read_bytes()
        with self.assertRaises(EvidenceError): self.run_export()
        self.assertEqual(before,(self.root/'evidence/data.jsonl').read_bytes())

if __name__ == '__main__':
    unittest.main(verbosity=2)
