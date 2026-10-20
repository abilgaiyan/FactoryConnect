import copy
import hashlib
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
from evidence_export import SETS, SQL_SHA256, EvidenceError, export, publication_lock
from provider_spool import ProviderSpool
from q2b_schema import CONTRACT, SERVER, DATABASE, MACHINE, PROCESSOR

SQL=(Path(__file__).parent.parent/'Q2B-revision-534-lineage-review.sql').read_bytes()
LOGIN='fixture\\restricted'
AUTH='synthetic-only'

def fixture():
    frames=[]
    for ordinal,name in enumerate(SETS):
        columns=[]; values=[]
        for i,(column,t) in enumerate(CONTRACT[name]):
            c={'ordinal':i,'name':column,'sql_type':t,'nullable':True,
               'provider_size':256,'provider_precision':None,'provider_scale':None}
            if t=='decimal': c.update(precision=20,scale=0,provider_precision=20,provider_scale=0)
            if t=='datetimeoffset': c.update(scale=7,provider_scale=7)
            columns.append(c)
            values.append({'nvarchar':'fixture','sql_variant':{'provider_type':'System.String','value':SERVER},
                           'bigint':'1','decimal':'1','datetimeoffset':'2026-10-16T00:00:00.0000001+00:00',
                           'date':'2026-10-16','varbinary':'00FF','uniqueidentifier':MACHINE,'bit':True}[t])
        def put(key,value): values[[c['name'] for c in columns].index(key)]=value
        put('ResultSet',name)
        if ordinal in (0,6): put('DatabaseName',DATABASE)
        if ordinal==0: put('OriginalLogin',LOGIN)
        if ordinal==1:
            put('ProcessorKey',PROCESSOR);put('StreamKey','metric-inputs')
            put('CommittedThroughMetricInputPosition','534')
        frames.extend([{'kind':'schema','ordinal':ordinal,'columns':columns},
                       {'kind':'row','ordinal':ordinal,'row_ordinal':0,'values':values}])
    return frames

def encoded(frames,**overrides):
    raw=b''.join((json.dumps(x,separators=(',',':'))+'\n').encode() for x in frames)
    terminal={'kind':'terminal','command_success':True,'reader_disposed':True,
              'connection_closed':True,'prefix_sha256':hashlib.sha256(raw).hexdigest(),
              'sql_sha256':SQL_SHA256,'row_counts':[1]*7,'authorization_id':AUTH,
              'provider':'synthetic'}
    terminal.update(overrides)
    return raw+(json.dumps(terminal,separators=(',',':'))+'\n').encode()

class AdapterTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.root=Path(self.temp.name)
        self.addCleanup(self.temp.cleanup)
    def read(self,raw):
        p=self.root/'spool';p.write_bytes(raw)
        Path(str(p)+'.success.json').write_text(json.dumps({
            'capture_success':True,'spool_sha256':hashlib.sha256(raw).hexdigest(),
            'sql_sha256':SQL_SHA256,'authorization_id':AUTH}))
        return ProviderSpool(p,expected_login=LOGIN,expected_authorization_id=AUTH)
    def test_valid_terminal_then_export(self):
        r=self.read(encoded(fixture()))
        result=export(r,r.schemas,SQL,self.root/'evidence',context={'fixture':True},finalize=r.finalize)
        self.assertEqual([1]*7,result['row_counts'])
        manifest=json.loads((self.root/'evidence/manifest.json').read_bytes())
        self.assertEqual('NotEstablished',manifest['terminal_receipt']['historical_correctness'])
        self.assertEqual('typed-reader-capture-only',manifest['scope'])
    def test_missing_or_late_command_error(self):
        raw=encoded(fixture())
        for bad in (raw[:raw.rfind(b'{"kind":"terminal"')],
                    raw+b'{"kind":"error"}\n',
                    encoded(fixture(),command_success=False),
                    encoded(fixture(),reader_disposed=False),
                    encoded(fixture(),connection_closed=False)):
            with self.assertRaises((EvidenceError,ValueError)):self.read(bad)
    def test_tampering_and_truncation(self):
        raw=encoded(fixture())
        for bad in (raw[:-1],raw.replace(b'fixture',b'changed',1)):
            with self.assertRaises(EvidenceError): self.read(bad)
    def test_schema_type_name_order_nullability_and_scale(self):
        for field,value in [('name','changed'),('sql_type','varchar'),('ordinal',99),('nullable',None)]:
            frames=fixture();frames[0]['columns'][1][field]=value
            with self.assertRaises(EvidenceError):self.read(encoded(frames))
        frames=fixture()
        frames[2]['columns'][-1]['scale']=6  # cut Position must be decimal(20,0)
        with self.assertRaises(EvidenceError):self.read(encoded(frames))
    def test_target_login_cut_authorization(self):
        for ordinal,key,value in [(0,'OriginalLogin','wrong'),(0,'DatabaseName','production'),
                                  (6,'is_read_only',False),(1,'ProcessorKey','wrong'),
                                  (1,'CommittedThroughMetricInputPosition','535')]:
            frames=fixture();cols=frames[ordinal*2]['columns'];row=frames[ordinal*2+1]
            row['values'][[c['name'] for c in cols].index(key)]=value
            with self.assertRaises(EvidenceError):self.read(encoded(frames))
        with self.assertRaises(EvidenceError): self.read(encoded(fixture(),authorization_id='wrong'))
    def test_singleton_and_count_failure(self):
        frames=fixture();frames.insert(2,copy.deepcopy(frames[1]));frames[2]['row_ordinal']=1
        with self.assertRaises(EvidenceError):self.read(encoded(frames,row_counts=[2,1,1,1,1,1,1]))
        with self.assertRaises(EvidenceError):self.read(encoded(fixture(),row_counts=[1]*6))
    def test_provider_float_rejected(self):
        frames=fixture();cols=frames[2]['columns'];row=frames[3]
        row['values'][[c['name'] for c in cols].index('CommittedThroughMetricInputPosition')]=534.0
        with self.assertRaises(EvidenceError):self.read(encoded(frames))
    def test_missing_completion_receipt(self):
        p=self.root/'spool';p.write_bytes(encoded(fixture()))
        with self.assertRaises(EvidenceError):
            ProviderSpool(p,expected_login=LOGIN,expected_authorization_id=AUTH)
    def test_finalizer_error_never_publishes(self):
        r=self.read(encoded(fixture()))
        def fail(_):raise RuntimeError('late provider error')
        with self.assertRaises(RuntimeError):
            export(r,r.schemas,SQL,self.root/'evidence',context={},finalize=fail)
        self.assertFalse((self.root/'evidence').exists())
        self.assertEqual([],list(self.root.glob('.q2b-incomplete-*')))
    def test_exclusive_lock_and_stale_lock_no_steal(self):
        output=self.root/'evidence'
        with publication_lock(output):
            with self.assertRaises(FileExistsError):
                with publication_lock(output):pass
        lock=self.root/'evidence.publish.lock';lock.write_text('stale-do-not-delete')
        with self.assertRaises(FileExistsError):
            with publication_lock(output):pass
        self.assertEqual('stale-do-not-delete',lock.read_text())
    def test_crossprocess_publication_lock(self):
        import subprocess,sys
        code="from evidence_export import publication_lock; from pathlib import Path; import sys;\nwith publication_lock(Path(sys.argv[1])):\n print('LOCKED',flush=True)\n sys.stdin.readline()\n"
        child=subprocess.Popen([sys.executable,'-c',code,str(self.root/'evidence')],
                               stdin=subprocess.PIPE,stdout=subprocess.PIPE,text=True)
        try:
            self.assertEqual('LOCKED',child.stdout.readline().strip())
            with self.assertRaises(FileExistsError):
                with publication_lock(self.root/'evidence'):pass
            child.communicate('\n',timeout=5)
            self.assertEqual(0,child.returncode)
        finally:
            if child.poll() is None:child.kill();child.wait(timeout=5)
    def test_interrupted_writer_does_not_publish(self):
        import subprocess,sys
        code="from evidence_export import export; from test_evidence_export import fixture,SQL; import os,sys;\ns,r=fixture()\nexport(r,s,SQL,sys.argv[1],context={},finalize=lambda _:os._exit(85))\n"
        result=subprocess.run([sys.executable,'-c',code,str(self.root/'evidence')],timeout=10)
        self.assertEqual(85,result.returncode)
        self.assertFalse((self.root/'evidence').exists())
        self.assertTrue(list(self.root.glob('.q2b-incomplete-*')))
    def test_rename_failure_cleanup(self):
        r=self.read(encoded(fixture()))
        with patch('evidence_export.os.rename',side_effect=OSError('synthetic rename failure')):
            with self.assertRaises(OSError):
                export(r,r.schemas,SQL,self.root/'evidence',context={},finalize=r.finalize)
        self.assertFalse((self.root/'evidence').exists())
        self.assertFalse((self.root/'evidence.publish.lock').exists())
        self.assertEqual([],list(self.root.glob('.q2b-incomplete-*')))

if __name__=='__main__':unittest.main(verbosity=2)
