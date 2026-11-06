"""One finite admitted executor. First red stops substantive steps; preservation is conditional."""
import hashlib,json,os,pathlib,signal,stat,sys,tarfile,time
from capture import ROOT,save,stamp,invoke,boundary,digest
from durable_publication import bind_parent,create_file,parent_barrier,close_parent,verify_file
B=json.loads((ROOT/'execution-binding.json').read_text());first_red=None;signals=[];started=time.monotonic();native_terminal=False;stage_started=False;native_started=False
finalizing=False;terminal_exit_armed=False;late_signal_fd=None;late_signal_parent=None;late_signal_identity=None;signal_record_errors=[];terminal_transition_error=None;preservation_attempted=False
class Stop(Exception):pass
def red(name,reason,rc=1):
 global first_red
 if first_red is None:
  first_red={'utc':stamp(),'gate':name,'reason':str(reason),'actualExit':rc,'noRetry':True};save('external-STOP.json',first_red)
 raise Stop(reason)
def step(name,preservation=False):
 if first_red and not preservation:raise Stop('first red: '+first_red['gate'])
 assert time.monotonic()-started<3880,'finite phase expired'
 spec=json.loads((ROOT/(name+'.spec.json')).read_text());rc=1
 try:rc=invoke(name+'-executor-wrapper',[sys.executable,'-B',str(ROOT/'capture.py'),name,name+'.spec.json'])
 finally:save(name+'.executor-wrapper-exit.code',str(rc))
 if signals:red(name,'received native signal; no forwarding or retry',128+signals[0])
 if rc!=0 and not preservation:red(name,'actual child exit '+str(rc),rc)
 return rc
def caught(n,unused):
 signals.append(n)
 try:
  if finalizing:
   # This pre-opened own adjacent fd is never sealed/rewritten with FIRST.
   raw=(str(n)+'\n').encode('ascii');assert late_signal_fd is not None
   verify_file(late_signal_parent,str(ROOT)+'.late-signals.jsonl',late_signal_fd,late_signal_identity)
   assert os.write(late_signal_fd,raw)==len(raw),'late signal short write'
   os.fsync(late_signal_fd)
   parent_barrier(late_signal_parent)
   verify_file(late_signal_parent,str(ROOT)+'.late-signals.jsonl',late_signal_fd,late_signal_identity)
  else:
   with (ROOT/'executor.native-signals.jsonl').open('ab') as f:f.write((json.dumps({'received':n,'signalsSent':[],'utc':stamp()})+'\n').encode());f.flush();os.fsync(f.fileno())
 except BaseException as error:
  signal_record_errors.append({'type':type(error).__name__,'reason':str(error),'errno':getattr(error,'errno',None)})
 finally:
  # A signal between the last live predicate and sys.exit still overrides zero.
  if terminal_exit_armed:raise SystemExit(128+n)
def live_signal_state():
 return {'signalsReceived':list(signals),'signalRecordErrors':list(signal_record_errors),'lateSignalPath':str(ROOT)+'.late-signals.jsonl','terminalTransitionError':terminal_transition_error,'preservationAttempted':preservation_attempted,'lateSignalPublication':'file-fsync+verified-directory-fsync; filesystem crash guarantee UNRUN','signalForwarding':'NONE','originalSnapshotAndFIRSTPreserved':True}
for n in (signal.SIGTERM,signal.SIGINT,signal.SIGHUP):signal.signal(n,caught)
def obj(name):return json.loads((ROOT/(name+'.stdout')).read_text())
def stage_typed():
 lines=(ROOT/'stage.stderr').read_text().splitlines();rows=[]
 for line in lines:
  try:r=json.loads(line)
  except ValueError:continue
  if 'ACTUAL_STAGE_TERMINAL' in r:rows.append(r['ACTUAL_STAGE_TERMINAL'])
 assert len(rows)==1;v=rows[0];wait=json.loads((ROOT/'stage.actual-wait.json').read_text())
 assert wait['actualReturnCode']==v['actualExitCode']==0 and not v['signalsReceived'] and not v['signalsSent'] and v['phase']=='complete-source-copy-only'
 assert v['manifestCheckRc']==0 and len(v['manifestCheckStdout'].splitlines())==37 and all(x.endswith(': OK') for x in v['manifestCheckStdout'].splitlines())
 assert v['baseline']['oomKill']==v['afterTransfer']['oomKill']==0
 save('actual-typed-stage-proof.json',{'senderWait':wait,'receiverTerminal':v,'typedAgreement':True,'stillProvisionalUntilExternalFINAL':True})
 return v
def remote_terminal(post):
 f=post['files'];a=json.loads(f['acceptance']['text']);outer=json.loads(f['outerActualExit']['text']);rc=int(f['records/exit.code']['text']);wait=json.loads((ROOT/'native.actual-wait.json').read_text())
 assert type(outer['actualOuterReturnCode']) is int and type(a['workloadExit']) is int and type(a['acceptanceExit']) is int
 assert wait['actualReturnCode']==outer['actualOuterReturnCode']==a['acceptanceExit'] and rc==a['workloadExit']
 assert a['firstVerificationRc']==0 and a['manifestSelfExcluded'] and a['firstEvidencePreserved'] and a['noRegeneration'] and a['entryCount']>0
 for n,h in a['firstHashes'].items():assert f['records/'+n]['sha256']==h
 lines=f['records/SHA256SUMS']['text'].splitlines();assert len(lines)==a['entryCount'] and not any(x.endswith('  ./SHA256SUMS') for x in lines)
 assert f['records/terminal.txt']['text']
 save('actual-terminal-FIRST-prerequisites.json',{'nativeWait':wait,'outer':outer,'acceptance':a,'remoteFirstEntryCount':len(lines),'strictReleaseMayRunOnce':True})
 return a
save('finite-phase-start.json',{'utc':stamp(),'monotonic':started,'maximumSeconds':3880,'noAllocationIssuedByExecutor':True})
try:
 boundary('executor-start');step('inputs');step('route-config')
 assert (ROOT/'route-config.stdout').read_text().strip()=='f7-fixture.invalid'
 save('native-route-proof.json',{'route':'direct-local-native','host':'f7-fixture.invalid','noSSH':True,'namespaceMapping':'NONE; all declared paths/process records belong to this native host'})
 step('admission');step('allocation-copy');stage_started=True;step('stage');typed=stage_typed();step('poststage');post=obj('poststage');assert post['passed'] and post['oomKill']==0
 assert json.loads(post['files']['transferReceipt']['text'])==typed and post['allStageFiles0444'] and post['stageStoredFiles']==38
 expected=json.loads((pathlib.Path(B['stageBinding'])/'expected-stage-members.json').read_text());assert post['allStageSha256']=={n:v['sha256'] for n,v in expected.items()}
 step('inputs-before-work');step('prework');native_started=True
 try:step('native')
 except Stop:pass
 # No substantive step follows native first red. These are bounded preservation/final-boundary calls only.
 postrc=step('postnative',preservation=True)
 if postrc!=0:
  if first_red is None:
   try:red('postnative','external boundary red',postrc)
   except Stop:pass
 else:
  post=obj('postnative');acceptance=remote_terminal(post);native_terminal=True
  releaserc=step('release',preservation=True)
  if releaserc!=0 and first_red is None:
   try:red('release','strict one-shot release red',releaserc)
   except Stop:pass
except Stop:pass
except BaseException as error:
 if first_red is None:
  try:red('executor',type(error).__name__+': '+str(error))
  except Stop:pass
# Terminal raw evidence capture never opens another native workload or retries a failed control.
if stage_started:
 try:
  rc=step('rawrecords',preservation=True)
  if rc==0:
   terminal_rows=[]
   for line in (ROOT/'rawrecords.stderr').read_text().splitlines():
    try:value=json.loads(line)
    except ValueError:continue
    if 'RAWRECORDS_CAPTURE_TERMINAL' in value:terminal_rows.append(value)
   assert len(terminal_rows)==1;terminal=terminal_rows[0];assert terminal['RAWRECORDS_CAPTURE_TERMINAL']==0 and terminal['originalFIRSTBefore']==terminal['originalFIRSTAfter']
   expected={x['path']:x for x in terminal['files']};assert len(expected)==len(terminal['files']) and sum(x['bytes'] for x in expected.values())<=402653184
   extracted=ROOT/'captured-rawrecords';extracted.mkdir();seen=set()
   with tarfile.open(ROOT/'rawrecords.stdout','r:gz') as archive:
    for member in archive:
     rel=pathlib.PurePosixPath(member.name);assert member.isfile() and not rel.is_absolute() and '..' not in rel.parts and member.name in expected and member.name not in seen;seen.add(member.name)
     item=expected[member.name];assert member.size==item['bytes'];q=extracted/rel;q.parent.mkdir(parents=True,exist_ok=True)
     with archive.extractfile(member) as incoming,q.open('xb') as out:
      h=hashlib.sha256();length=0
      while True:
       chunk=incoming.read(1048576)
       if not chunk:break
       out.write(chunk);h.update(chunk);length+=len(chunk)
      out.flush();os.fsync(out.fileno())
     assert length==item['bytes'] and h.hexdigest()==item['sha256'];q.chmod(item['mode'])
   assert seen==set(expected);save('rawrecords-capture-proof.json',{'terminal':terminal,'losslessFilesChecked':len(seen),'rawArchiveSha256':digest(ROOT/'rawrecords.stdout'),'noOriginalAuditorReplay':True})
  elif first_red is None:
   try:red('rawrecords','one raw capture red',rc)
   except Stop:pass
 except BaseException as error:
  if first_red is None:
   try:red('rawrecords',str(error))
   except Stop:pass
 try:
  rc=step('external-final',preservation=True)
  if rc!=0 and first_red is None:
   try:red('external-final','actual external FINAL red',rc)
   except Stop:pass
 except BaseException as error:
  if first_red is None:
   try:red('external-final',str(error))
   except Stop:pass
# Guard setup, state save and finalization as one terminal transition. The live
# signal list remains red even if creating/publishing its sidecar fails.
try:
 late_signal_path=pathlib.Path(str(ROOT)+'.late-signals.jsonl')
 late_signal_parent=bind_parent(late_signal_path)
 late_signal_fd=create_file(late_signal_parent,late_signal_path)
 late_signal_identity=verify_file(late_signal_parent,late_signal_path,late_signal_fd)
 os.fsync(late_signal_fd)
 parent_barrier(late_signal_parent)
 verify_file(late_signal_parent,late_signal_path,late_signal_fd,late_signal_identity)
 finalizing=True
 save('executor-state.json',{'utc':stamp(),'firstRed':first_red,'stageStarted':stage_started,'nativeStarted':native_started,'nativeTerminalAndFIRSTBound':native_terminal,'signalsReceived':signals,'signalsSent':[],'finiteElapsedSeconds':time.monotonic()-started,'noSubstantiveStepAfterFirstRed':True,'nativeWorkloadRetries':0,'releaseAttempts':int((ROOT/'release.invoked.json').exists()),'scopeEnd':True})
 from finalize_evidence import main
 code=main(live_signal_state=live_signal_state)
except BaseException as error:
 terminal_transition_error={'type':type(error).__name__,'reason':str(error),'errno':getattr(error,'errno',None),'lateSignalPath':str(ROOT)+'.late-signals.jsonl','lateSignalFdObtained':late_signal_fd is not None,'partialPublicationStatus':'UNKNOWN','noReopenRetryOverwrite':True}
 signal_record_errors.append(terminal_transition_error)
 preservation_attempted=True
 from preserve_incomplete import main as preserve
 # The applicable incomplete path is attempted exactly once. Its own failure
 # is not converted to a synthetic successful preservation or retried.
 code=preserve(error,terminal_transition=live_signal_state())
# Arm before evaluating live state: every later handled signal raises RED.
terminal_exit_armed=True
outer_elapsed=time.monotonic()-started
if not (0<=outer_elapsed<=3880):
 code=1
 raw=(json.dumps({'outerObservedElapsedSeconds':outer_elapsed,'unchangedMaximumSeconds':3880,'actualExecutorExit':1,'originalResultAndFIRSTPreserved':True,'charge':'ordinary retainedPacketAndBundle; root actual afterreceiptFINAL mandatory'})+'\n').encode()
 assert late_signal_fd is not None,'late signal setup incomplete; outer elapsed UNKNOWN'
 verify_file(late_signal_parent,str(ROOT)+'.late-signals.jsonl',late_signal_fd,late_signal_identity)
 assert os.write(late_signal_fd,raw)==len(raw),'outer elapsed record short write'
 os.fsync(late_signal_fd)
 parent_barrier(late_signal_parent)
 verify_file(late_signal_parent,str(ROOT)+'.late-signals.jsonl',late_signal_fd,late_signal_identity)
# Close failures remain actual RED. No reopen, close retry or postseal rewrite.
try:
 if late_signal_fd is not None:os.close(late_signal_fd)
except BaseException as error:
 code=1;signal_record_errors.append({'type':type(error).__name__,'reason':str(error),'errno':getattr(error,'errno',None),'phase':'late-signal-file-close'})
finally:
 try:
  if late_signal_parent is not None:close_parent(late_signal_parent)
 except BaseException as error:
  code=1;signal_record_errors.append({'type':type(error).__name__,'reason':str(error),'errno':getattr(error,'errno',None),'phase':'late-signal-parent-close'})
sys.exit(128+signals[0] if signals else (1 if signal_record_errors else code))
