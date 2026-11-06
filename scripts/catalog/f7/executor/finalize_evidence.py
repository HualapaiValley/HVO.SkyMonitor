"""Generic evidence finalization of this exact b60 native plan; never resumes workload or repairs FIRST."""
import base64,errno,hashlib,json,os,pathlib,stat,subprocess,time,zlib
from capture import ROOT,save,digest,boundary,stamp
from durable_publication import bind_parent,create_file,parent_barrier,close_parent,verify_file


def decode_checksum_stdout(value):
 assert type(value) is dict and set(value)=={'codec','rawBytes','rawSha256','lineCount','okRows','packedBytes','packedSha256','data'}, 'checksum codec shape'
 assert value['codec']=='gzip-mtime0-base64-utf8-v1'
 for name in ('rawBytes','lineCount','okRows','packedBytes'):assert type(value[name]) is int and value[name]>=0, 'checksum codec numeric field'
 assert value['rawBytes']<=1048576 and type(value['data']) is str and len(value['data'])<=32768
 for name in ('rawSha256','packedSha256'):assert type(value[name]) is str and len(value[name])==64 and all(x in '0123456789abcdef' for x in value[name])
 packed=base64.b64decode(value['data'].encode('ascii'),validate=True)
 assert len(packed)==value['packedBytes'] and hashlib.sha256(packed).hexdigest()==value['packedSha256']
 assert packed[:3]==b'\x1f\x8b\x08' and packed[4:8]==b'\0'*4, 'deterministic gzip header'
 decoder=zlib.decompressobj(16+zlib.MAX_WBITS)
 raw=decoder.decompress(packed,value['rawBytes']+1)
 assert decoder.eof and not decoder.unused_data and not decoder.unconsumed_tail, 'truncated/extra/overflow gzip'
 assert len(raw)==value['rawBytes'] and hashlib.sha256(raw).hexdigest()==value['rawSha256']
 assert raw.count(b'\n')==value['lineCount'] and raw.count(b': OK')==value['okRows']
 raw.decode('utf-8',errors='strict')
 return raw

def validate_afterseal_receipt(receipt,actual,manifest_rows):
 assert type(actual) is int and actual==0, 'actual external wrapper nonzero'
 assert receipt['passed'] is True and type(receipt['fullManifestActualShaCExit']) is int and receipt['fullManifestActualShaCExit']==0, 'actual afterseal/check red'
 assert type(receipt['fullManifestRows']) is int
 assert type(manifest_rows) is int and manifest_rows>=0 and receipt['fullManifestRows']==manifest_rows
 assert 'fullManifestCheckStdout' not in receipt, 'legacy unbounded inline output'
 raw=decode_checksum_stdout(receipt['fullManifestCheckStdoutEncoded'])
 assert raw.count(b': OK')==manifest_rows
 return raw


def four_file_receipt_accounting(files):
 assert len(files)==4, 'exact four postseal files required'
 blocks=[path.stat().st_blocks for path in files]
 assert all(type(n) is int and n>=0 for n in blocks), 'postseal allocated blocks UNKNOWN'
 allocated=sum(n*512 for n in blocks)
 return {'allocatedBytes':allocated,'capBytes':65536,'passed':allocated<=65536,'rawBlocks':blocks}

def main(live_signal_state=None):
 B=json.loads((ROOT/'execution-binding.json').read_text());packet=pathlib.Path(B['sourcePacket']);pins=json.loads((packet/'candidate-pins.json').read_text());state=json.loads((ROOT/'executor-state.json').read_text());limits=pins['resource'];raw=ROOT/'captured-rawrecords'/'1186-b60-f7-accounting-receipt-v7-e2d998ed'/'records';validation=[]
 def load(name):
  p=ROOT/name
  return json.loads(p.read_text()) if p.is_file() else None
 def check(condition,label):
  validation.append({'check':label,'passed':bool(condition)})
  return bool(condition)
 original=None;final=load('external-final.stdout');release=load('release.stdout');native=load('native.actual-wait.json');first={};checks=[]
 if raw.is_dir() and (raw/'SHA256SUMS').is_file():
  side=raw.parent/'records.acceptance.txt';original=json.loads(side.read_text()) if side.is_file() else None
  for line in (raw/'SHA256SUMS').read_text().splitlines():
   h,n=line.split('  ',1);relative=pathlib.PurePosixPath(n);assert not relative.is_absolute() and '..' not in relative.parts
   p=raw/relative;checks.append({'path':n,'expected':h,'actual':digest(p),'matches':digest(p)==h})
  first={n:digest(raw/n) for n in ['SHA256SUMS','exit.code','terminal.txt']}
  check(bool(original) and len(checks)==original['entryCount'] and all(x['matches'] for x in checks) and not any(x['path']=='./SHA256SUMS' for x in checks),'copied FIRST byte exact/self excluded/exact declared entry count')
  if original:check(first==original['firstHashes'],'copied FIRST hashes equal original acceptance')
 save('captured-FIRST-verification.json',{'entries':len(checks),'checks':checks,'passed':bool(checks) and all(x['matches'] for x in checks),'originalAuditorReplayed':False,'originalFIRSTHashes':first})
 steps=json.loads((packet/'commands.json').read_text())['orderedSteps'];gates=[]
 for step in steps:
  p=raw/(step['id']+'.observer.actual-exit.code');rc=int(p.read_text()) if p.is_file() else None
  gates.append({'id':step['id'],'status':'NOTRUN' if rc is None else 'PASS' if rc==0 else 'FAIL','actualObserverExit':rc,'selectedArgv':step['argv']})
 check(len(gates)==119,'exact selected119 F7 plan')
 if state['nativeStarted']:
  check(state['nativeTerminalAndFIRSTBound'],'actual native/outer/workload/acceptance FIRST prerequisites')
  check(bool(native) and native['actualReturnCode']==0,'actual native exit0 required for candidate pass')
  check(bool(original) and original['workloadExit']==original['acceptanceExit']==0 and original['firstVerificationRc']==0,'original component acceptance0 required for candidate pass')
  check(all(x['status']=='PASS' for x in gates),'all119 fresh F7/setup/validation gates PASS required')
  if release:
   check(release['firstHashesUnchanged'] and release['originalRootReverificationRc']==0 and release['completeGroupOrSessionScan']['complete'] and not release['completeGroupOrSessionScan']['unknowns'] and not release['recordedIdentityUnknowns'] and not release['residualGroupOrSessionMembers'] and not release['lockAndWaiterLines'] and all(x['oldBirthAbsent'] for x in release['allRecordedIdentities']),'strict release completeness/absence/unchanged FIRST')
  else:check(False,'strict release missing/NOTRUN')
  wait=load('release.actual-wait.json');check(bool(wait) and wait['actualReturnCode']==0,'one release typed actual exit0')
 if state['stageStarted']:
  wait=load('stage.actual-wait.json');check(bool(wait) and wait['actualReturnCode']==0,'one stage typed actual exit0')
  check(bool(final) and final['passed'] and final['oomKill']==0,'remote external FINAL resource/OOM0')
  wait=load('external-final.actual-wait.json');check(bool(wait) and wait['actualReturnCode']==0,'remote external FINAL typed actual exit0')
  if final and final.get('stageSourceHashes'):
   check(all(digest(packet/n)==h for n,h in final['stageSourceHashes'].items()),'final source runtime hashes unchanged')
   check(all(x['status']=='GONE' or x.get('oldBirthAbsent') for x in final['recordedWrapperPhysicalObservations']),'only recorded remote wrapper births absent')
 else:check(False,'remote stage/native UNRUN; no candidate pass')
 physical=[]
 for role,path in [('native-capture-wrapper',ROOT/'native-before.local-boundary.json'),('native-isolated-timeout-env',ROOT/'native.actual-child-before-work.json')]:
  if not path.is_file():continue
  obj=json.loads(path.read_text());i=obj['own'] if 'own' in obj else obj['actual'];q=pathlib.Path('/proc')/str(i['pid']);v={'role':role,'recorded':i,'status':'UNKNOWN'}
  try:
   st=q.stat();s=(q/'stat').read_text();f=s[s.rfind(')')+2:].split();v.update({'status':'PRESENT','root':{'uid':st.st_uid,'inode':st.st_ino,'dev':st.st_dev},'rawStat':s,'currentBirth':int(f[19]),'oldBirthAbsent':int(f[19])!=i['birth'],'rawStatus':(q/'status').read_text()})
  except OSError as error:
   v['rawError']={'errno':error.errno,'reason':str(error)}
   try:q.stat()
   except OSError as root_error:
    v['rootError']={'errno':root_error.errno,'reason':str(root_error)}
    if root_error.errno==errno.ENOENT:v['status']='GONE';v['oldBirthAbsent']=True
  physical.append(v)
 save('native-recorded-wrapper-physical-observation.json',{'utc':stamp(),'observations':physical,'namespaceMapping':'NONE; local recorded PIDs only','wholeCohortOrContinuousCompleteness':'UNPROVEN'})
 if physical:check(all(x['status']=='GONE' or x.get('oldBirthAbsent') for x in physical),'native recorded wrapper births absent')
 elapsed=time.monotonic()-load('finite-phase-start.json')['monotonic'];check(elapsed<=3880,'finite phase maximum3880 at receipt')
 passed=state['firstRed'] is None and all(x['passed'] for x in validation)
 code=0 if passed else 1
 if not passed and state['firstRed'] is None and not (ROOT/'external-STOP.json').exists():save('external-STOP.json',{'utc':stamp(),'gate':'final-evidence-validation','reason':'required typed/source/resource/release agreement missing or red','noRetry':True,'originalFIRSTPreserved':first})
 result={'allocationId':load('allocation.json')['allocationId'],'verdict':'PASS / component evidence only; provisional until actual external after-seal FINAL' if passed else 'FAIL or INCOMPLETE / allocation consumed','executorActualExit':code,'head':pins['head'],'tree':pins['tree'],'base':pins['base'],'state':state,'gates':gates,'validation':validation,'gatesSummary':{k:sum(x['status']==k for x in gates) for k in ['PASS','FAIL','NOTRUN']},'originalFIRSTHashes':first,'originalFIRSTEntryCount':len(checks),'releaseAttempts':state['releaseAttempts'],'releaseStatus':'NOTRUN / INCOMPLETE' if not release else 'actual result retained; no workload upgrade','resourceLimits':limits,'frozenRemoteOOM':0,'remoteFINAL':{k:final[k] for k in ['utc','availableBytes','memAvailableBytes','oomKill','workspaceAllocatedBytes','componentsBytes','nativeExecutorAllocatedBytes','passed','violations']} if final else None,'samplingLimit':'Boundary observations only; continuous resource/process peaks and cohort completeness UNPROVEN','rawSelfAndWait4NotOwnRSS':True,'hostMemAvailableDropNotAttributed':True,'newScope':{'oldFunctionalCasesCarriedOnly':416,'ManualExecutions':24,'F7Comparisons':2,'ABBAProcessesPerComparison':12,'newPerformanceReleaseRoots':2,'BclCpuProofBuilds':1},'oldEvidence':'Old952 red/old18a AST red/old12 core controls immutable carry only; no fresh credit','targetProfile':'F7 included catalog operations only; development/v1 CI remains separate and no CI dispatch','noRetryRepairCleanupOrFurtherAllocation':True,'sourceOrOwnerEdited':False,'finiteScopeEnded':True}
 save('result.json',result)
 files=sorted(p for p in ROOT.rglob('*') if p.is_file())
 for p in files:assert not p.is_symlink();p.chmod(0o444)
 with (ROOT/'FIRST-SHA256SUMS').open('x') as f:
  for p in files:f.write(digest(p)+'  ./'+str(p.relative_to(ROOT))+'\n')
  f.flush();os.fsync(f.fileno())
 (ROOT/'FIRST-SHA256SUMS').chmod(0o444)
 save('executor-first-seal.json',{'utc':stamp(),'manifestSha256':digest(ROOT/'FIRST-SHA256SUMS'),'entryCount':len(files),'selfExcluded':True,'originalRemoteFIRSTHashes':first,'immutable':True})
 try:local=boundary('postcapture-FINAL-native')
 except BaseException as error:
  local=load('postcapture-FINAL-native.local-boundary.json');code=1
  save('finalizer-native-boundary-STOP.json',{'utc':stamp(),'type':type(error).__name__,'reason':str(error),'originalResultProvisionalPreserved':True,'noRetry':True})
 reserve=65536;combined=None
 if final:
  combined=dict(final['componentsBytes']);combined['evidence']+=local['executorAllocatedBytes']+reserve-final['nativeExecutorAllocatedBytes']
  if final['workspaceAllocatedBytes']-final['nativeExecutorAllocatedBytes']+local['executorAllocatedBytes']+reserve>limits['workspaceBytes'] or not all(0<=combined[k]<=m for k,m in limits['componentCeilingsBytes'].items()):
   code=1;save('finalizer-combined-boundary-STOP.json',{'utc':stamp(),'reason':'combined charged resources red','originalResultProvisionalPreserved':True,'noRetry':True})
 save('externalactual-finalreceipt.json',{'utc':stamp(),'executorActualExit':code,'originalFIRSTHashes':first,'executorFirstManifestSha256':digest(ROOT/'FIRST-SHA256SUMS'),'nativeFinalBoundary':local,'remoteFINAL':result['remoteFINAL'],'combinedConservativeComponentsBytes':combined,'finalReceiptAndFullManifestReservedBytes':reserve,'allInnerZerosProvisionalUntilActualExternalAfterSeal':True,'continuousPeaks':'UNPROVEN','noRetryRepairCleanupOrFurtherAllocation':True})
 files=sorted(p for p in ROOT.rglob('*') if p.is_file())
 for p in files:p.chmod(0o444)
 with (ROOT/'SHA256SUMS').open('x') as f:
  for p in files:f.write(digest(p)+'  ./'+str(p.relative_to(ROOT))+'\n')
  f.flush();os.fsync(f.fileno())
 (ROOT/'SHA256SUMS').chmod(0o444)
 check_manifest=subprocess.run(['sha256sum','-c','SHA256SUMS'],cwd=ROOT,capture_output=True,text=True,timeout=30)
 assert check_manifest.returncode==0 and check_manifest.stdout.count(': OK')==len(files)
 # Actual final after full seal is a separate read-only child, not an assertion derived from inner0.
 external=pathlib.Path(str(ROOT)+'.postseal.final.json');stderr=pathlib.Path(str(ROOT)+'.postseal.final.stderr');waitpath=pathlib.Path(str(ROOT)+'.postseal.actual-wait.json')
 before=pathlib.Path(str(ROOT)+'.postseal.child-before-work.json');readfd,writefd=os.pipe()
 trampoline='import os,sys;fd=int(sys.argv[1]);assert os.read(fd,1)==b"1";os.close(fd);os.execvp(sys.argv[2],sys.argv[2:])'
 from capture import identity
 with external.open('xb') as out,stderr.open('xb') as err:
  child=subprocess.Popen([os.sys.executable,'-B','-c',trampoline,str(readfd),os.sys.executable,'-B',str(ROOT/'external-afterseal.py'),str(ROOT)],stdout=out,stderr=err,pass_fds=(readfd,),start_new_session=True);os.close(readfd)
  i=identity(child.pid)
  with before.open('x') as f:json.dump({'actual':i,'barrier':'owned pipe, unreleased','noNamespaceMapping':True},f);f.flush();os.fsync(f.fileno())
  assert i['ppid']==os.getpid() and i['uid']==os.getuid() and i['pgid']==child.pid and i['sid']==child.pid
  os.write(writefd,b'1');os.close(writefd);pid,status,usage=os.wait4(child.pid,0);actual=os.waitstatus_to_exitcode(status);child.returncode=actual
 with waitpath.open('x') as f:json.dump({'utc':stamp(),'pid':pid,'rawWaitStatus':status,'WIFEXITED':os.WIFEXITED(status),'WEXITSTATUS':os.WEXITSTATUS(status) if os.WIFEXITED(status) else None,'WIFSIGNALED':os.WIFSIGNALED(status),'WTERMSIG':os.WTERMSIG(status) if os.WIFSIGNALED(status) else None,'actualReturnCode':actual,'wait4usage':{k:getattr(usage,k) for k in ['ru_utime','ru_stime','ru_maxrss','ru_inblock','ru_oublock','ru_nvcsw','ru_nivcsw']},'rawWait4NotOwnRSS':True,'ownSignalsSent':[]},f);f.flush();os.fsync(f.fileno())
 four=four_file_receipt_accounting([external,stderr,waitpath,before]);actual_extra=four['allocatedBytes'];receipt_errors=[]
 assert four['capBytes']==reserve
 if not four['passed']:receipt_errors.append({'type':'ReceiptAllocationExcess','actualAllocatedBytes':actual_extra,'unchangedCapBytes':reserve,'rawBlocks':four['rawBlocks']})
 try:
  receipt=json.loads(external.read_bytes());validate_afterseal_receipt(receipt,actual,len(files))
 except BaseException as error:receipt_errors.append({'type':type(error).__name__,'reason':str(error)})
 if receipt_errors:
  # Keep all raw external bytes and all four actual receipts. No trimming/rewriting.
  fallback=pathlib.Path(str(ROOT)+'.postseal.codec-STOP.json')
  note={'utc':stamp(),'actualExecutorExit':1,'actualExternalAfterSealExit':actual,'errors':receipt_errors,'actualFourFilesAllocatedBytes':actual_extra,'unchangedFourFileCapBytes':reserve,'fullRawReceipt':{'path':str(external),'bytes':external.stat().st_size,'sha256':digest(external)},'originalResultAndFIRSTPreserved':True,'fullRawFailureIsRed':True,'noRetry':True,'accounting':'ordinary common-root retainedPacketAndBundle; unchanged aggregate/component caps; root actual after-receipt FINAL mandatory'}
  with fallback.open('x') as stream:json.dump(note,stream,indent=2);stream.write('\n');stream.flush();os.fsync(stream.fileno())
  code=1
 # Compute hashes/serialization before the terminal elapsed boundaries. All four
 # files, decoder checks and actual wait have finished; result/FIRST stay exact.
 terminal={'FINITE_TESTEND':code,'resultSha256':digest(ROOT/'result.json'),'executorManifestSha256':digest(ROOT/'SHA256SUMS'),'fullEntries':len(files),'actualShaCExit':check_manifest.returncode,'actualExternalAfterSealExit':actual,'externalReceipt':str(external),'externalWait':str(waitpath),'originalFIRSTHashes':first,'firstRedPreserved':state['firstRed'],'noFurtherActionAuthorized':True,'terminalIsProvisionalUntilActualWaitAndPostEmissionElapsed':True}
 latepath=pathlib.Path(str(ROOT)+'.late-STOP.json');late_fd=None;late_parent=None;late_identity=None
 def late_stop(phase,observed,live=None):
  nonlocal code,late_fd,late_parent,late_identity
  code=1
  note={'utc':stamp(),'phase':phase,'liveSignalState':live,'actualExecutorExit':1,'actualExternalAfterSealExit':actual,'observedElapsedSeconds':observed,'unchangedMaximumSeconds':3880,'overrunSeconds':max(0,observed-3880),'originalResultAndFIRSTPreserved':True,'noRetry':True,'accounting':'ordinary common-root retainedPacketAndBundle; unchanged aggregate/component caps; root actual after-receipt FINAL mandatory'}
  # Fresh create-only first record; subsequent observed late samples append on
  # this same owned fd. Never reopen/rewrite result, FIRST or a foreign sidecar.
  if late_fd is None:
   late_parent=bind_parent(latepath);late_fd=create_file(late_parent,latepath)
   late_identity=verify_file(late_parent,latepath,late_fd)
  verify_file(late_parent,latepath,late_fd,late_identity)
  body=(json.dumps(note,sort_keys=True)+'\n').encode('utf-8')
  assert os.write(late_fd,body)==len(body),'late STOP short write; original partial retained'
  os.fsync(late_fd);parent_barrier(late_parent)
  verify_file(late_parent,latepath,late_fd,late_identity)
 phase_start=load('finite-phase-start.json')['monotonic']
 observed=time.monotonic()-phase_start
 signals_before=live_signal_state() if live_signal_state is not None else {'signalsReceived':state.get('signalsReceived',[]),'signalRecordErrors':[]}
 if not (0<=observed<=3880) or actual!=0 or signals_before['signalsReceived'] or signals_before['signalRecordErrors']:late_stop('after-actual-wait-four-files-decoder-and-terminal-hashes',observed,signals_before)
 terminal['liveSignalStateBeforeEmission']=signals_before
 terminal['FINITE_TESTEND']=code;terminal['observedElapsedSecondsBeforeEmission']=observed
 try:
  # This record's inner zero is explicitly provisional. The return/outer actual
  # wait is authoritative; even terminal emission cost is checked before zero.
  print(json.dumps(terminal),flush=True)
  observed=time.monotonic()-phase_start
  signals_after=live_signal_state() if live_signal_state is not None else {'signalsReceived':state.get('signalsReceived',[]),'signalRecordErrors':[]}
  if not (0<=observed<=3880) or signals_after['signalsReceived'] or signals_after['signalRecordErrors']:late_stop('after-terminal-emission-final-boundary',observed,signals_after)
 finally:
  try:
   if late_fd is not None:os.close(late_fd)
  finally:
   if late_parent is not None:close_parent(late_parent)
 return code
if __name__=='__main__':raise SystemExit(main())
