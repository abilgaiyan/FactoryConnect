"""Strict offline adapter for the bounded provider spool; never opens a connection."""
import hashlib
import json
import uuid
from decimal import Decimal
from pathlib import Path
from evidence_export import EvidenceError, SETS, SQL_SHA256
from q2b_schema import CONTRACT, SERVER, DATABASE, MACHINE, PROCESSOR

def strict_json(raw):
    def unique(pairs):
        value = {}
        for k,v in pairs:
            if k in value: raise EvidenceError('Duplicate JSON key')
            value[k]=v
        return value
    return json.loads(raw, object_pairs_hook=unique,
                      parse_constant=lambda x: (_ for _ in ()).throw(EvidenceError('Nonfinite JSON')))

class ProviderSpool:
    def __init__(self, path, *, expected_login, expected_authorization_id, max_bytes=10*1024*1024):
        self.path=Path(path)
        if self.path.stat().st_size>max_bytes: raise EvidenceError('Spool limit')
        with self.path.open('rb') as stream: raw=stream.read(max_bytes+1)
        if len(raw)>max_bytes: raise EvidenceError('Spool limit')
        lines=raw.splitlines(keepends=True)
        if not lines or any(not x.endswith(b'\n') for x in lines):
            raise EvidenceError('Incomplete spool line')
        frames=[strict_json(x) for x in lines]
        terminal=frames[-1]
        if terminal.get('kind')!='terminal' or terminal.get('command_success') is not True:
            raise EvidenceError('No command terminal success')
        if terminal.get('sql_sha256')!=SQL_SHA256:
            raise EvidenceError('Provider SQL identity mismatch')
        if terminal.get('prefix_sha256')!=hashlib.sha256(b''.join(lines[:-1])).hexdigest():
            raise EvidenceError('Provider prefix digest mismatch')
        if terminal.get('reader_disposed') is not True or terminal.get('connection_closed') is not True:
            raise EvidenceError('Provider completion not finalized')
        if (not expected_authorization_id or terminal.get('authorization_id')!=expected_authorization_id):
            raise EvidenceError('Authorization receipt mismatch')
        self.terminal=terminal
        self.raw_sha256=hashlib.sha256(raw).hexdigest()
        receipt_path=Path(str(self.path)+'.success.json')
        if not receipt_path.exists() or receipt_path.stat().st_size>65536:
            raise EvidenceError('Missing/bounded provider completion receipt')
        receipt=strict_json(receipt_path.read_bytes())
        if (receipt.get('capture_success') is not True or receipt.get('spool_sha256')!=self.raw_sha256
                or receipt.get('sql_sha256')!=SQL_SHA256
                or receipt.get('authorization_id')!=expected_authorization_id):
            raise EvidenceError('Provider completion receipt mismatch')
        self.completion_receipt=receipt
        self.login=expected_login
        if not isinstance(expected_login,str) or not expected_login:
            raise EvidenceError('Approved login required')
        self.schemas=[]; self.rows=[]
        for frame in frames[:-1]:
            kind=frame.get('kind')
            if kind=='schema':
                index=len(self.schemas)
                if index>=7 or frame.get('ordinal')!=index: raise EvidenceError('Schema ordering')
                name=SETS[index]
                cols=frame.get('columns')
                if not isinstance(cols,list) or [(c.get('name'),c.get('sql_type')) for c in cols]!=CONTRACT[name]:
                    raise EvidenceError('Independent SELECT schema mismatch')
                for i,c in enumerate(cols):
                    if c.get('ordinal')!=i or type(c.get('nullable')) is not bool:
                        raise EvidenceError('Missing provider nullability/ordinal')
                    if c['sql_type']=='decimal' and (c.get('precision')!=20 or c.get('scale')!=0):
                        raise EvidenceError('Unexpected position decimal metadata')
                    if c['sql_type']=='datetimeoffset' and c.get('scale')!=7:
                        raise EvidenceError('Timestamp metadata mismatch')
                    if c['sql_type'] in ('nvarchar','varchar','varbinary','binary') and 'provider_size' not in c:
                        raise EvidenceError('Missing size metadata')
                self.schemas.append({'name':name,'columns':cols}); self.rows.append([])
            elif kind=='row':
                if not self.rows or frame.get('ordinal')!=len(self.rows)-1:
                    raise EvidenceError('Row ordering')
                if frame.get('row_ordinal')!=len(self.rows[-1]): raise EvidenceError('Dropped/repeated row ordinal')
                values=frame.get('values')
                cols=self.schemas[-1]['columns']
                if not isinstance(values,list) or len(values)!=len(cols): raise EvidenceError('Truncated provider row')
                typed=[]
                for c,v in zip(cols,values):
                    t=c['sql_type']
                    if v is None: typed.append(None); continue
                    if t in ('decimal','numeric'):
                        if type(v) is not str: raise EvidenceError('Provider decimal passed through number')
                        typed.append(Decimal(v))
                    elif t in ('bigint','int','smallint','tinyint'):
                        if type(v) is not str: raise EvidenceError('Provider integer passed through number')
                        typed.append(int(v))
                    elif t in ('binary','varbinary'):
                        if type(v) is not str: raise EvidenceError('Provider binary not hex')
                        typed.append(bytes.fromhex(v))
                    elif t=='uniqueidentifier':
                        if type(v) is not str: raise EvidenceError('Provider GUID not string')
                        typed.append(uuid.UUID(v))
                    else: typed.append(v)
                self.rows[-1].append(typed)
                if len(self.rows[-1])>1000: raise EvidenceError('Provider row limit')
            else: raise EvidenceError('Unexpected frame/late error')
        if (len(self.schemas)!=7 or not isinstance(terminal.get('row_counts'),list) or
                any(type(x) is not int for x in terminal['row_counts']) or terminal.get('row_counts')!=[len(x) for x in self.rows]):
            raise EvidenceError('Incomplete result inventory')
        self._check_identity()

    def _check_identity(self):
        for i in (0,1,2,5,6):
            if len(self.rows[i])!=1: raise EvidenceError('Singleton cardinality')
        def record(i):
            return dict(zip([c['name'] for c in self.schemas[i]['columns']],self.rows[i][0]))
        for i in (0,6):
            row=record(i)
            if row['SqlServer']!={'provider_type':'System.String','value':SERVER} or row['DatabaseName']!=DATABASE or row['is_read_only'] is not True:
                raise EvidenceError('Approved target/end-target mismatch')
        if record(0)['OriginalLogin']!=self.login: raise EvidenceError('Approved reader mismatch')
        cut=record(1)
        if (cut['MetricAggregationProcessorRowId']!=1 or cut['MetricInputStreamRowId']!=1 or
            cut['ProcessorKey']!=PROCESSOR or cut['StreamKey']!='metric-inputs' or
            str(cut['MachineId'])!=MACHINE or cut['CommittedThroughMetricInputPosition']!=Decimal(534)):
            raise EvidenceError('Approved cut identity mismatch')
        count=record(2)['AdmittedContributionCount']
        if count!=len(self.rows[3]) or count!=record(5)['ContributionCount'] or not 0<=count<=1000:
            raise EvidenceError('Membership count reconciliation')
        # Do not discard SQL anomalies or claim historical authority from these checks.

    def __iter__(self):
        return iter(zip(self.schemas,self.rows))

    def finalize(self,counts):
        if counts!=[len(x) for x in self.rows]: raise EvidenceError('Postcapture count mismatch')
        return {'provider_terminal':self.terminal,'provider_spool_sha256':self.raw_sha256, 'completion_receipt':self.completion_receipt,
                'identity_checks':'target-reader-cut-singletons-and-counts',
                'historical_correctness':'NotEstablished'}
