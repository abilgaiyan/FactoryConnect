"""Offline completion of a provider spool. NEVER opens a database."""
import argparse
import hashlib
from pathlib import Path
from evidence_export import export
from provider_spool import ProviderSpool

def main():
    p=argparse.ArgumentParser()
    p.add_argument('--spool',required=True);p.add_argument('--sql',required=True)
    p.add_argument('--output',required=True);p.add_argument('--expected-login',required=True)
    p.add_argument('--authorization-id',required=True)
    a=p.parse_args()
    reader=ProviderSpool(a.spool,expected_login=a.expected_login,
                         expected_authorization_id=a.authorization_id)
    receipt=export(reader,reader.schemas,Path(a.sql).read_bytes(),a.output,
        context={'authorization_id':a.authorization_id,'expected_login':a.expected_login,
                 'spool_sha256':reader.raw_sha256,'source':'SqlClient-provider-spool'},
        finalize=reader.finalize)
    print(receipt)

if __name__=='__main__':
    main()
