import pathlib,os,re,json,subprocess,copy
import tempfile
import argparse
import hashlib
parser = argparse.ArgumentParser()
parser.add_argument("--results", type=pathlib.Path)
options = parser.parse_args()
data = pathlib.Path(__file__).resolve().parent
scratch = tempfile.TemporaryDirectory(prefix="w1747-drill-")
p = pathlib.Path(scratch.name)
plan_path = data.parent / "development-v1-merge-queue-drill.md"
plan = plan_path.read_text()
blocks = re.findall(r'```bash\n(.*?)\n```', plan, re.S)
assert len(blocks)==7,len(blocks)
for i,b in enumerate(blocks):
 r=subprocess.run(['bash','-n'],input=b,text=True,capture_output=True);assert r.returncode==0,(i,r.stderr)
print('PASS bash syntax: 7 plan blocks')
base_rule=json.loads((data/'ruleset-26838822.before.json').read_text());base_protection=json.loads((data/'branch-protection.before.json').read_text());index=json.loads((data/'rulesets-index.before.json').read_text())
for block in blocks:
 assert not re.search(r'gh\s+(workflow|run)\s+(run|rerun)',block)
 assert 'enqueuePullRequest' not in block
assert json.loads((data/'queue-observation.historical.json').read_text())['data']['repository']['mergeQueue']['entries']['nodes']==[]
print('PASS scope: no workflow, admission or merge commands; read-only queue observation valid')
bin_dir=p/'mock-bin';bin_dir.mkdir(exist_ok=True)
fake=r'''#!/usr/bin/python3
import sys,json,os,pathlib,copy
args=sys.argv[1:];p=pathlib.Path(os.environ['DRILL_FAKE_STATE']);x=json.loads(p.read_text());record={'args':args}
def save():p.write_text(json.dumps(x))
def output(v):print(json.dumps(v));save();sys.exit(0)
if args[:2]==['pr','list']:output([{'number':1,'autoMergeRequest':{'enabledAt':'now'}}] if x.get('auto') else [])
if args[0]=='api':args.pop(0)
if args[0]=='graphql':
 query=args[args.index('-f')+1];assert query.startswith('query=query { repository(owner:"HualapaiValley"')
 output({'data':{'repository':{'mergeQueue':{'entries':{'nodes':[{'id':'busy'}] if x.get('busy') else [],'pageInfo':{'hasNextPage':False}}}}}})
method=args[args.index('--method')+1];route=args[2]
if method=='GET':
 if route=='repos/HualapaiValley/HVO.SkyMonitor':output({'permissions':{'admin':True}})
 if 'rulesets?' in route:output(x['index'])
 if route.endswith('/26838822'):
  if x.get('corrupt_pending'):
   x['corrupt_pending']=False;x['rule']['name']='unexpected-name'
   if x.get('classic_drift'):
    x['protection']['enforce_admins']['enabled']=False;x['protection']['required_signatures']['enabled']=True
  output(x['rule'])
 if route.endswith('/protection'):output(x['protection'])
 raise SystemExit('Unmapped GET '+route)
x['writes'].append({'method':method,'route':route})
if route.endswith('/required_signatures'):
 x['protection']['required_signatures']['enabled']=method=='POST';output({})
body=json.loads(pathlib.Path(args[args.index('--input')+1]).read_text())
if route.endswith('/26838822'):
 old=copy.deepcopy(x['rule']);x['rule'].update(body)
 if not any(r['type']=='merge_queue' for r in body['rules']):
  expected = {k:v for k,v in old.items() if k in body}
  expected['rules'] = [r for r in old['rules'] if r['type'] != 'merge_queue']
  assert body == expected
  if x.get('corrupt_once'):x['corrupt_pending']=True;x['corrupt_once']=False
 output(x['rule'])
if route.endswith('/protection'):
 for k,v in body.items():
  if k=='enforce_admins' or k in ('required_linear_history','allow_force_pushes','allow_deletions','block_creations','required_conversation_resolution','lock_branch','allow_fork_syncing'):
   x['protection'][k]={'enabled':v}
  else:x['protection'][k]=v
 for c in x['protection']['required_status_checks']['checks']:
  if c.get('app_id')==-1:c['app_id']=None
 x['protection']['required_status_checks']['contexts']=[c['context'] for c in x['protection']['required_status_checks']['checks']]
 output(x['protection'])
raise SystemExit('Unmapped write '+route)
'''
(bin_dir/'gh').write_text(fake);(bin_dir/'gh').chmod(0o700)
def state(**extra):return dict(rule=copy.deepcopy(base_rule),protection=copy.deepcopy(base_protection),index=copy.deepcopy(index),writes=[],**extra)
def execute(block_ids,x):
 file=p/'drill-fake-state.json';file.write_text(json.dumps(x));env=dict(os.environ,PATH=str(bin_dir)+':'+os.environ['PATH'],DRILL_FAKE_STATE=str(file))
 r=subprocess.run(['bash'],cwd=data.parents[2],input='\n'.join(blocks[i] for i in block_ids),text=True,capture_output=True,env=env)
 (p/'drill-last-output.log').write_text(r.stdout+r.stderr)
 return r,json.loads(file.read_text())
def canon(x):
 if isinstance(x,dict):return {k:canon(v) for k,v in x.items() if k not in ('url','contexts_url') and v is not None}
 if isinstance(x,list):return [canon(v) for v in x]
 return x
r,x=execute(range(7),state());assert r.returncode==0,(r.returncode,r.stdout,r.stderr);assert len(x['writes'])==3,x['writes'];assert x['rule']==base_rule;assert canon(x['protection'])==canon(base_protection)
print('PASS full cutover + rollback: 3 fake ruleset PUTs; exact baseline restored')
for name,s in [('busy queue',state(busy=True)),('auto-merge enabled',state(auto=True)),('index truncated',state()),('required check wrong',state())]:
 if name=='index truncated':s['index']*=50
 if name=='required check wrong':s['rule']['rules'][3]['parameters']['required_status_checks'][0]['context']='unexpected'
 r,x=execute(range(5),s);assert r.returncode!=0 and x['writes']==[],(name,r.stdout,r.stderr,x['writes']);print('PASS precondition rejection: '+name+'; 0 writes')
r,x=execute(range(5),state(corrupt_once=True,classic_drift=True));assert r.returncode!=0 and len(x['writes'])==1,(r.stdout,r.stderr)
r,x=execute([0,5,6],x);assert r.returncode==0,(r.stdout,r.stderr);assert x['rule']==base_rule;assert canon(x['protection'])==canon(base_protection)
assert any(w['route'].endswith('/required_signatures') and w['method']=='DELETE' for w in x['writes'])
print('PASS bad readback then rollback: full ruleset, classic protection and signature state restored')
print('Drill plan checks: 6 behavior cases plus 7 syntax blocks passed; 0 failed, 0 skipped')

if options.results:
    inputs = [plan_path, pathlib.Path(__file__), *sorted(data.glob("*.before.json")),
              *sorted(data.glob("*.restore.json")), data / "ruleset.fallback.json",
              data / "snapshot.json", data / "queue-observation.historical.json"]
    results = {"liveDrill": "KNOWN LIMIT / HELD", "runnerCapacity": "KNOWN LIMIT (Atlas)",
               "execution": "private fake API only", "behaviorCasesPassed": 6,
               "bashBlocksPassed": 7, "failed": 0, "skipped": 0,
               "cases": [{"name": name, "result": "PASS"} for name in
                         ["full cutover and rollback", "occupied queue", "auto-merge enabled",
                          "enumeration at limit", "unexpected required check",
                          "bad readback and full restoration"]],
               "sha256": {str(path.relative_to(data.parent)): hashlib.sha256(path.read_bytes()).hexdigest()
                          for path in inputs}}
    options.results.write_text(json.dumps(results, indent=2) + "\n")
