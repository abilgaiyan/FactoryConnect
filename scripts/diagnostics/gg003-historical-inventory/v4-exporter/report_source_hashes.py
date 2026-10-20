"""Offline source identity report. Does not connect to SQL."""
import hashlib
import json
import os
import platform
from pathlib import Path
root=Path(__file__).parent
files=sorted([*root.glob('*.py'),*root.glob('SqlReader/*.cs'),*root.glob('SqlReader/*.csproj')])
print(json.dumps({'commit':os.environ.get('GITHUB_SHA','local-unpinned'),
 'python':platform.python_version(),'os':platform.platform(),
 'files':[{'path':str(p.relative_to(root)).replace('\\','/'),
           'bytes':len(p.read_bytes()),'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in files]},
 indent=2))
