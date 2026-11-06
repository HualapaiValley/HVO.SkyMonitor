"""Actual read-only native FINAL after the full immutable executor manifest."""
from durable_publication import bind_parent,create_file,parent_barrier,publish_bytes,close_parent,verify_file
import base64,errno,gzip,hashlib,json,os,pathlib,resource,selectors,signal,stat,subprocess,sys,time

def encode_checksum_stdout(raw):
 assert type(raw) is bytes and len(raw)<=1048576, 'checksum raw encoding bound'
 raw.decode('utf-8',errors='strict')
 packed=gzip.compress(raw,compresslevel=9,mtime=0)
 data=base64.b64encode(packed).decode('ascii')
 assert len(data)<=32768, 'checksum encoded field bound'
 return {'codec':'gzip-mtime0-base64-utf8-v1','rawBytes':len(raw),'rawSha256':hashlib.sha256(raw).hexdigest(),'lineCount':raw.count(b'\n'),'okRows':raw.count(b': OK'),'packedBytes':len(packed),'packedSha256':hashlib.sha256(packed).hexdigest(),'data':data}

def retained_path_observation(path,screen):
 """Only explicit ENOENT with a verified parent proves absence; all else UNKNOWN."""
 screen=max(4096,screen)
 try:
  info=path.lstat()
  assert stat.S_ISREG(info.st_mode) and info.st_uid==os.getuid(),'retention file type/UID UNKNOWN'
  assert type(info.st_blocks) is int and info.st_blocks>=0,'retention allocated blocks UNKNOWN'
  return {'exists':True,'provenAbsent':False,'allocatedBytes':info.st_blocks*512,'chargedConservativeBytes':info.st_blocks*512,'bytes':info.st_size,'rawStat':{'mode':info.st_mode,'uid':info.st_uid,'dev':info.st_dev,'inode':info.st_ino,'blocks':info.st_blocks}}
 except BaseException as error:
  detail={'type':type(error).__name__,'reason':str(error),'errno':getattr(error,'errno',None)}
  if isinstance(error,OSError) and error.errno==errno.ENOENT:
   try:
    parent=path.parent.lstat()
    assert stat.S_ISDIR(parent.st_mode) and parent.st_uid==os.getuid(),'retention parent type/UID UNKNOWN'
    return {'exists':False,'provenAbsent':True,'allocatedBytes':0,'chargedConservativeBytes':0,'absenceErrno':errno.ENOENT,'rawInspectionError':detail,'verifiedParent':{'mode':parent.st_mode,'uid':parent.st_uid,'dev':parent.st_dev,'inode':parent.st_ino}}
   except BaseException as parent_error:detail['parentError']={'type':type(parent_error).__name__,'reason':str(parent_error),'errno':getattr(parent_error,'errno',None)}
  return {'exists':None,'provenAbsent':False,'allocatedBytes':None,'allocatedBytesStatus':'UNKNOWN','chargedConservativeBytes':screen,'rawInspectionError':detail}

def retention_journal_write(journal,record):
 # Fixed byte records remain useful if JSON/base64/allocation/output fails later.
 assert type(record) is bytes and len(record)<=512
 verify_file(journal['parent'],journal['path'],journal['fd'],journal['fileIdentity'])
 written=os.write(journal['fd'],record)
 assert written==len(record),'retention journal short write; existing partial record preserved'
 os.fsync(journal['fd'])
 verify_file(journal['parent'],journal['path'],journal['fd'],journal['fileIdentity'])

def create_retention_journal(root):
 path=pathlib.Path(str(root)+'.postseal.check-retention.journal')
 parent=bind_parent(path);fd=None
 try:
  fd=create_file(parent,path)
  journal={'fd':fd,'path':str(path),'parent':parent,'fileIdentity':verify_file(parent,path,fd),'noRewriteOrRetry':True,'initialPublicationBarrierComplete':False}
  retention_journal_write(journal,b'V6_OBTAINED_EVIDENCE_UNCOMMITTED\nSTREAMS_PARTIAL_OR_UNRETAINED_UNKNOWN\nCHECKER_ACTUAL_WAIT_UNKNOWN_UNTIL_TERMINAL_COMMIT\nNO_ZERO_OR_RELEASE_CREDIT\n')
  parent_barrier(parent)
  journal['initialPublicationBarrierComplete']=True
  return journal
 except BaseException:
  # No checker starts, no original partial file is reopened or retried. Actual
  # outer exception/status remains RED; partial name crash survival is UNKNOWN.
  try:
   if fd is not None:os.close(fd)
  finally:close_parent(parent)
  raise

def charge_retained_observation(meta,value,limits,allocated,reserved,components,final):
 charged=meta['chargedConservativeBytes']
 assert type(charged) is int and charged>=0,'retention charge UNKNOWN'
 if components is not None:components['retainedPacketAndBundle']+=charged
 value['adjacentRetentionChargedBytes']=value.get('adjacentRetentionChargedBytes',0)+charged
 total=value['adjacentRetentionChargedBytes']
 if allocated+reserved+total>536870912 or (final and (final['workspaceAllocatedBytes']-final['nativeExecutorAllocatedBytes']+allocated+reserved+total>limits['workspaceBytes'] or any(components[k]<0 or components[k]>m for k,m in limits['componentCeilingsBytes'].items()))):value['violations'].append('ordinary charged retention allocation excess')
 if meta['allocatedBytes'] is None:value['violations'].append('ordinary retained allocation UNKNOWN; actual root afterreceiptFINAL mandatory')

def retain_obtained_evidence(stdout,stderr,checker,value,root,limits,allocated,reserved,components,final,journal):
 """Raw streams commit before any encoding/mode/output tail; no retry/truncation."""
 metadata={'fullObtainedStreamsRetained':False,'actualTerminalRetained':False,'artifacts':{},'errors':[],'journal':{'path':journal['path'],'initialState':'UNCOMMITTED / PARTIAL_OR_UNRETAINED_UNKNOWN','initialPublicationBarrierComplete':journal['initialPublicationBarrierComplete'],'publicationSemantics':journal['parent']['publicationSemantics']}}
 value['obtainedEvidence']=metadata
 for name,raw in [('stdout',stdout),('stderr',stderr)]:
  path=pathlib.Path(str(root)+'.postseal.check-'+name+'.raw');screen=max(4096,((len(raw)+4095)//4096)*4096);item={'path':str(path),'fullRawRetained':False,'obtainedBytes':len(raw),'obtainedSha256':hashlib.sha256(raw).hexdigest()};metadata['artifacts'][name]=item
  try:
   publish_bytes(journal['parent'],path,raw)
   observation=retained_path_observation(path,screen)
   assert observation['exists'] is True and observation['bytes']==len(raw) and observation['allocatedBytes'] is not None,'obtained raw publication/accounting UNKNOWN'
   item['fullRawRetained']=True
   retention_journal_write(journal,b'STDOUT_FULL_COMMITTED\n' if name=='stdout' else b'STDERR_FULL_COMMITTED\n')
  except BaseException as error:
   item['retentionError']={'type':type(error).__name__,'reason':str(error),'errno':getattr(error,'errno',None)};metadata['errors'].append(item['retentionError']);value['violations'].append('obtained raw partial/unretained UNKNOWN')
   observation=retained_path_observation(path,screen)
  item.update(observation);charge_retained_observation(item,value,limits,allocated,reserved,components,final)
 path=pathlib.Path(str(root)+'.postseal.check-terminal.json');item={'path':str(path),'fullTerminalRetained':False};metadata['artifacts']['terminal']=item;screen=65536
 try:
  body=(json.dumps(checker,sort_keys=True,allow_nan=False)+'\n').encode('utf-8')
  assert len(body)<=1048576,'checker terminal bounded raw metadata'
  screen=max(4096,((len(body)+4095)//4096)*4096)
  publish_bytes(journal['parent'],path,body)
  observation=retained_path_observation(path,screen)
  assert observation['exists'] is True and observation['bytes']==len(body) and observation['allocatedBytes'] is not None,'terminal publication/accounting UNKNOWN'
  item.update(fullTerminalRetained=True,bytes=len(body),sha256=hashlib.sha256(body).hexdigest());metadata['actualTerminalRetained']=True
  retention_journal_write(journal,b'CHECKER_TERMINAL_JSON_FULL_COMMITTED\n')
 except BaseException as error:
  item['retentionError']={'type':type(error).__name__,'reason':str(error),'errno':getattr(error,'errno',None)};metadata['errors'].append(item['retentionError']);value['violations'].append('checker actual terminal unretained UNKNOWN')
  observation=retained_path_observation(path,screen)
  try:
   actual=checker.get('actualWait');suffix=(str(actual['rawWaitStatus'])+':'+str(actual['actualReturnCode'])).encode('ascii') if actual is not None else b'UNKNOWN'
   retention_journal_write(journal,b'CHECKER_OBSERVED_RAW_STATUS_AND_RETURN:'+suffix+b'\nFULL_TERMINAL_UNRETAINED_UNKNOWN\n')
  except BaseException:pass # Initial publication API barrier completed; filesystem crash survival UNRUN.
 item.update(observation);charge_retained_observation(item,value,limits,allocated,reserved,components,final)
 metadata['fullObtainedStreamsRetained']=all(metadata['artifacts'][n]['fullRawRetained'] for n in ['stdout','stderr'])
 return metadata

def retain_checksum_failure(stdout,stderr,error,value,root,limits,allocated,reserved,components,final,journal=None):
 # Metadata is attached BEFORE all fallible base64/JSON/encode/screen allocations.
 path=pathlib.Path(str(root)+'.postseal.check-output-fallback.json');screen=65536
 meta={'path':str(path),'fullRawRetained':False,'allocatedBytes':None,'allocatedBytesStatus':'UNKNOWN','chargedConservativeBytes':screen,'actualShaCExit':value.get('fullManifestActualShaCExit'),'checkerTerminal':value.get('checkerTerminal'),'captureComplete':value.get('checkerTerminal',{}).get('captureComplete'),'stdoutBytes':len(stdout),'stdoutSha256':hashlib.sha256(stdout).hexdigest(),'stderrBytes':len(stderr),'stderrSha256':hashlib.sha256(stderr).hexdigest()}
 value['checksumOutputFallback']=meta;value['passed']=False
 try:
  meta['codecFailure']={'type':type(error).__name__,'reason':str(error),'errno':getattr(error,'errno',None)}
  body=(json.dumps(dict(meta,stdoutBase64=base64.b64encode(stdout).decode('ascii'),stderrBase64=base64.b64encode(stderr).decode('ascii')),sort_keys=True,separators=(',',':'),allow_nan=False)+'\n').encode()
  screen=max(4096,((len(body)+4095)//4096)*4096)
  total=value.get('adjacentRetentionChargedBytes',0)
  assert allocated+reserved+total+screen<=536870912,'full raw fallback cannot fit unchanged native cap'
  if final:
   assert final['workspaceAllocatedBytes']-final['nativeExecutorAllocatedBytes']+allocated+reserved+total+screen<=limits['workspaceBytes'],'full raw fallback workspace excess'
   assert components['retainedPacketAndBundle']+screen<=limits['componentCeilingsBytes']['retainedPacketAndBundle'],'full raw fallback component excess'
  assert journal is not None,'fallback verified publication parent UNKNOWN'
  publish_bytes(journal['parent'],path,body)
  observation=retained_path_observation(path,screen)
  assert observation['exists'] is True and observation['bytes']==len(body) and observation['allocatedBytes'] is not None,'full raw fallback publication/accounting UNKNOWN'
  meta.update(fullRawRetained=True,bytes=len(body),sha256=hashlib.sha256(body).hexdigest())
 except BaseException as failure:
  meta['retentionError']={'type':type(failure).__name__,'reason':str(failure),'errno':getattr(failure,'errno',None)}
  observation=retained_path_observation(path,screen)
  if journal is not None:
   try:retention_journal_write(journal,b'FALLBACK_PARTIAL_OR_UNRETAINED_UNKNOWN_RED\n')
   except BaseException:pass
 meta.update(observation);charge_retained_observation(meta,value,limits,allocated,reserved,components,final)
 value['violations'].append('full checksum postcapture/encoding failure; no truncation or waiver')


def checker_identity_at_barrier(pid,argv,executable,observations):
 proc=pathlib.Path('/proc')/str(pid)
 expected_argv=b'\0'.join(x.encode('utf-8',errors='surrogateescape') for x in argv)+b'\0'
 for unused in range(2):
  entry={'expectedArgv':argv,'expectedExe':executable,'pid':pid,'complete':False,'phase':'root-stat'};observations.append(entry)
  root=proc.stat();entry['root']={'uid':root.st_uid,'dev':root.st_dev,'inode':root.st_ino,'mode':root.st_mode}
  entry['phase']='stat-read';raw=(proc/'stat').read_text();entry['rawStat']=raw;fields=raw[raw.rfind(')')+2:].split()
  entry['phase']='status-read';status=(proc/'status').read_text();entry['rawStatus']=status
  entry['phase']='cmdline-read';cmdline=(proc/'cmdline').read_bytes();entry['cmdlineBytes']=len(cmdline);entry['cmdlineSha256']=hashlib.sha256(cmdline).hexdigest();entry['cmdlineBase64']=base64.b64encode(cmdline).decode('ascii')
  entry['phase']='exe-read';exe=proc/'exe';exe_target=os.readlink(str(exe));entry['exeTarget']=exe_target;exe_stat=exe.stat()
  uids=next([int(x) for x in line.split()[1:]] for line in status.splitlines() if line.startswith('Uid:'))
  assert len(cmdline)<=65536 and len(status.encode())<=65536,'owned barrier raw metadata bound'
  identity={'pid':pid,'birth':int(fields[19]),'uid':root.st_uid,'ppid':int(fields[1]),'pgid':int(fields[2]),'sid':int(fields[3]),'uids':uids}
  raw_root={'uid':root.st_uid,'dev':root.st_dev,'inode':root.st_ino,'mode':root.st_mode}
  actual_exe={'target':exe_target,'uid':exe_stat.st_uid,'dev':exe_stat.st_dev,'inode':exe_stat.st_ino,'mode':exe_stat.st_mode,'bytes':exe_stat.st_size}
  assert stat.S_ISDIR(root.st_mode) and identity['uid']==os.getuid() and uids==[os.getuid()]*4,'checker UID/root identity UNKNOWN'
  assert identity['ppid']==os.getpid() and identity['pgid']==pid and identity['sid']==pid,'checker parent/session UNKNOWN'
  assert cmdline==expected_argv,'checker full barrier argv mismatch'
  assert actual_exe==executable,'checker exe identity mismatch'
  entry.update(identity=identity,root=raw_root,exe=actual_exe,complete=True,phase='complete')
 stable=lambda item:(item['identity'],item['root'],item['cmdlineBase64'],item['exe'])
 assert stable(observations[0])==stable(observations[1]),'checker barrier stable identity metadata changed/UNKNOWN'
 return {'identity':observations[0]['identity'],'stableIdentity':stable(observations[0]),'observations':observations,'completeBarrierEvidence':True,'namespaceMapping':'NONE; exact own direct child only'}

def capture_checksum_checker(root,capture_state=None):
 """One owned checker, unchanged 30s boundary; observed bytes survive every failure.

 No communicate/run timeout exception owns the raw buffers. Nonblocking bounded
 drain and wait4 observations are explicit. UNKNOWN terminal/EOF stays RED.
 A pidfd bound while the child is stopped at our pipe authorizes only that child.
 """
 started=time.monotonic();deadline=started+30;buffers={'stdout':bytearray(),'stderr':bytearray()}
 terminal={'argv':['sha256sum','-c','SHA256SUMS'],'timeoutSeconds':30,'startedMonotonic':started,'timedOut':False,'captureComplete':False,'eof':{'stdout':False,'stderr':False},'actualReturnCode':None,'actualWait':None,'ownSignalsSent':[],'errors':[],'childIdentity':None,'barrierReleased':False,'drainLimitPerStreamBytes':1048576,'noRetry':True}
 if capture_state is not None:capture_state['buffers']=buffers;capture_state['terminal']=terminal
 child=None;pidfd=None;selector=None;readfd=None;writefd=None;streams={};owned=False
 def fault(phase,error):
  terminal['errors'].append({'phase':phase,'type':type(error).__name__,'reason':str(error),'errno':getattr(error,'errno',None)})
 def reap():
  if child is None or terminal['actualWait'] is not None:return
  pid,status,usage=os.wait4(child.pid,os.WNOHANG)
  if pid:
   assert pid==child.pid
   actual=os.waitstatus_to_exitcode(status);child.returncode=actual
   terminal['actualReturnCode']=actual
   terminal['actualWait']={'pid':pid,'rawWaitStatus':status,'WIFEXITED':os.WIFEXITED(status),'WEXITSTATUS':os.WEXITSTATUS(status) if os.WIFEXITED(status) else None,'WIFSIGNALED':os.WIFSIGNALED(status),'WTERMSIG':os.WTERMSIG(status) if os.WIFSIGNALED(status) else None,'actualReturnCode':actual,'wait4usage':{k:getattr(usage,k) for k in ['ru_utime','ru_stime','ru_maxrss','ru_inblock','ru_oublock','ru_nvcsw','ru_nivcsw']},'rawWait4NotOwnRSS':True}
 def read_one(stream,name):
  try:chunk=os.read(stream.fileno(),65536)
  except BlockingIOError:return
  if chunk:
   # Append every obtained byte BEFORE testing the bound. Never trim a read.
   buffers[name].extend(chunk)
   if len(buffers[name])>1048576:raise OverflowError('obtained checker stream exceeds bounded raw capture; retained prefix is partial')
  else:
   terminal['eof'][name]=True;selector.unregister(stream)
 def stop_owned():
  if child is None or terminal['actualWait'] is not None:return
  if not owned or pidfd is None:
   fault('signal-authority',RuntimeError('owned pidfd/identity unknown; no signal sent'));return
  entry={'signal':int(signal.SIGKILL),'pid':child.pid,'identity':terminal['childIdentity'],'authority':'exact owned child pidfd; no group/foreign signal','sent':False}
  terminal['ownSignalsSent'].append(entry)
  try:signal.pidfd_send_signal(pidfd,signal.SIGKILL,None,0);entry['sent']=True
  except BaseException as error:entry['error']={'type':type(error).__name__,'reason':str(error),'errno':getattr(error,'errno',None)};fault('own-checker-signal',error)
 try:
  readfd,writefd=os.pipe()
  trampoline='import os,sys;fd=int(sys.argv[1]);assert os.read(fd,1)==b"1";os.close(fd);os.execvp(sys.argv[2],sys.argv[2:])'
  command=[sys.executable,'-B','-c',trampoline,str(readfd),'sha256sum','-c','SHA256SUMS'];terminal['barrierCommand']=command
  executable_path=os.path.realpath(sys.executable);exe_info=os.stat(executable_path)
  expected_exe={'target':executable_path,'uid':exe_info.st_uid,'dev':exe_info.st_dev,'inode':exe_info.st_ino,'mode':exe_info.st_mode,'bytes':exe_info.st_size}
  child=subprocess.Popen(command,cwd=root,stdout=subprocess.PIPE,stderr=subprocess.PIPE,pass_fds=(readfd,),start_new_session=True)
  terminal['childPid']=child.pid;streams={'stdout':child.stdout,'stderr':child.stderr}
  os.close(readfd);readfd=None
  before_evidence=[];terminal['barrierIdentityEvidenceBeforePidfd']=before_evidence
  before_identity=checker_identity_at_barrier(child.pid,command,expected_exe,before_evidence);terminal['childIdentity']=before_identity
  pidfd=os.pidfd_open(child.pid,0)
  after_evidence=[];terminal['barrierIdentityEvidenceAfterPidfd']=after_evidence
  after_identity=checker_identity_at_barrier(child.pid,command,expected_exe,after_evidence);terminal['identityAfterPidfd']=after_identity
  assert before_identity['stableIdentity']==after_identity['stableIdentity'],'checker pidfd-open identity race/UNKNOWN';owned=True
  selector=selectors.DefaultSelector();streams={'stdout':child.stdout,'stderr':child.stderr}
  for name,stream in streams.items():os.set_blocking(stream.fileno(),False);selector.register(stream,selectors.EVENT_READ,name)
  assert time.monotonic()<deadline,'checker30 expired before own barrier release'
  assert os.write(writefd,b'1')==1;os.close(writefd);writefd=None;terminal['barrierReleased']=True
  while True:
   remaining=deadline-time.monotonic()
   if remaining<=0:
    terminal['timedOut']=True;fault('checker-timeout',TimeoutError('unchanged checker30 boundary'));break
   for key,unused in selector.select(min(remaining,0.05)):read_one(key.fileobj,key.data)
   reap()
   if terminal['actualWait'] is not None and all(terminal['eof'].values()):break
 except BaseException as error:fault('checker-capture-or-wait',error)
 finally:
  if terminal['timedOut'] or terminal['errors']:stop_owned()
  # No extra blocking grace or checker retry: at most 17 ready reads per pipe,
  # retaining the entire last read even if it crosses 1MiB. Any missing EOF is RED.
  if selector is not None:
   try:
    for unused in range(17):
     ready=selector.select(0)
     if not ready:break
     for key,events in ready:
      if len(buffers[key.data])<=1048576:read_one(key.fileobj,key.data)
   except BaseException as error:fault('bounded-terminal-drain',error)
  try:reap()
  except BaseException as error:fault('actual-terminal-wait',error)
  for name,stream in streams.items():
   try:stream.close()
   except BaseException as error:fault('pipe-close-'+name,error)
  if selector is not None:
   try:selector.close()
   except BaseException as error:fault('selector-close',error)
  for fd in (readfd,writefd,pidfd):
   if fd is not None:
    try:os.close(fd)
    except BaseException as error:fault('owned-fd-close',error)
  # Keep an unreaped owned child object alive for the wrapper lifetime; never
  # substitute Popen's implicit wait/poll for the recorded actual wait4.
  if child is not None and terminal['actualWait'] is None:_UNREAPED_OWN_CHECKERS.append(child)
 terminal['finishedMonotonic']=time.monotonic();terminal['elapsedSeconds']=terminal['finishedMonotonic']-started
 if terminal['elapsedSeconds']>30 and not terminal['timedOut']:
  terminal['timedOut']=True;fault('checker-tail-deadline',TimeoutError('capture/drain/wait tail exceeded unchanged checker30'))
 if terminal['actualWait'] is None:fault('checker-terminal-UNKNOWN',RuntimeError('no actual wait4 terminal observed; release/absence remains UNKNOWN'))
 terminal['captureComplete']=all(terminal['eof'].values())
 if not terminal['captureComplete']:fault('checker-streams-PARTIAL',RuntimeError('one or both pipe EOFs unknown; all obtained bytes retained without completeness claim'))
 return buffers['stdout'],buffers['stderr'],terminal

_UNREAPED_OWN_CHECKERS=[]

def main():
 ROOT=pathlib.Path(sys.argv[1]);result=json.loads((ROOT/'result.json').read_text());baseline=json.loads((ROOT/'local-baseline.json').read_text());limits=result['resourceLimits']
 fs=os.statvfs(ROOT);mem=dict(x.split(':',1) for x in pathlib.Path('/proc/meminfo').read_text().splitlines());vm=dict(x.split() for x in pathlib.Path('/proc/vmstat').read_text().splitlines());raw=pathlib.Path('/proc/self/stat').read_text();fields=raw[raw.rfind(')')+2:].split();status=pathlib.Path('/proc/self/status').read_text();locks=pathlib.Path('/proc/locks').read_text().splitlines();ownrss=next(int(x.split()[1])*1024 for x in status.splitlines() if x.startswith('VmRSS:'))
 allocated=int(subprocess.check_output(['du','-s','-B1',str(ROOT)],text=True).split()[0]);reserved=65536
 final=result['remoteFINAL'];components=dict(final['componentsBytes']) if final else None
 if components:components['evidence']+=allocated+reserved-final['nativeExecutorAllocatedBytes']
 value={'BS-F2':'actual-native-FINAL-after-executor-full-seal','utc':subprocess.check_output(['/usr/bin/date','-u','+%Y-%m-%dT%H:%M:%S.%NZ'],text=True).strip(),'hostname':subprocess.check_output(['hostname'],text=True).strip(),'own':{'pid':os.getpid(),'birth':int(fields[19]),'uid':os.getuid(),'ppid':int(fields[1]),'pgid':int(fields[2]),'sid':int(fields[3]),'rawStat':raw,'rawStatus':status},'ownLockLines':[x for x in locks if x.split()[4]==str(os.getpid())],'dfB1':subprocess.check_output(['df','-B1',str(ROOT)],text=True),'diskAvailableBytes':fs.f_bavail*fs.f_frsize,'memAvailableBytes':int(mem['MemAvailable'].split()[0])*1024,'oomKill':int(vm['oom_kill']),'nativeAllocatedBytes':allocated,'nativeOwnVmRSSBytes':ownrss,'rawSelfRusage':{k:getattr(resource.getrusage(resource.RUSAGE_SELF),k) for k in ['ru_utime','ru_stime','ru_maxrss','ru_inblock','ru_oublock','ru_nvcsw','ru_nivcsw']},'rawSelfNotOwnRSS':True,'hostMemAvailableChangesNotAttributed':True,'nativeOutputReserveBytes':reserved,'nativeCaps':{'workspace':536870912,'diskReserve':536870912,'hostRAMFloor':536870912,'ownVmRSS':268435456},'remoteFINAL':final,'combinedConservativeComponentsBytes':components,'fullManifestSha256':hashlib.sha256((ROOT/'SHA256SUMS').read_bytes()).hexdigest(),'continuousPeakOrWholeCohortCompleteness':'UNPROVEN; actual boundary only','violations':[]}
 if value['oomKill']!=baseline['oomKill']:value['violations'].append('native OOM changed')
 # Same native host now holds workload and executor; actual post-seal sample must retain the NEW workload reserves too.
 lock=pathlib.Path(limits['lockPath']);value['sharedHostLock']={'inode':lock.stat().st_ino,'uid':lock.stat().st_uid,'mode':lock.stat().st_mode,'symlink':lock.is_symlink(),'holderWaiterLines':[x for x in locks if ':'+str(limits['inode'])+' ' in x]}
 if value['hostname']!=limits['host'] or os.getuid()!=1000 or value['sharedHostLock']['inode']!=limits['inode'] or value['sharedHostLock']['uid']!=1000 or value['sharedHostLock']['symlink'] or not stat.S_ISREG(value['sharedHostLock']['mode']) or value['sharedHostLock']['holderWaiterLines']:value['violations'].append('shared native host/UID/lock red')
 if value['diskAvailableBytes']<limits['reserveBytes'] or value['memAvailableBytes']<limits['ramReserveBytes'] or value['oomKill']!=0:value['violations'].append('shared native actual FINAL reserve/OOM0 red')
 if allocated+reserved>536870912 or value['diskAvailableBytes']<1073741824 or value['memAvailableBytes']<536870912 or ownrss>268435456:value['violations'].append('native boundary red')
 if final:
  if final['oomKill']!=0 or not final['passed']:value['violations'].append('local workload final red or OOM0 changed')
  if final['workspaceAllocatedBytes']-final['nativeExecutorAllocatedBytes']+allocated+reserved>limits['workspaceBytes'] or any(components[k]<0 or components[k]>m for k,m in limits['componentCeilingsBytes'].items()):value['violations'].append('combined charged bytes excess')
 journal=create_retention_journal(ROOT)
 code=1;capture_state={};stdout=b'';stderr=b'';checker={'actualReturnCode':None,'actualWait':None,'captureComplete':False,'timedOut':False,'errors':[{'phase':'capture-not-committed','reason':'UNKNOWN'}]}
 try:
  journal_meta=retained_path_observation(pathlib.Path(journal['path']),4096)
  assert journal_meta['exists'] is True and journal_meta['allocatedBytes'] is not None,'journal publication UNKNOWN; checker UNRUN'
  assert journal['initialPublicationBarrierComplete'],'journal directory publication UNKNOWN; checker UNRUN'
  value['retentionJournal']=dict(journal_meta,path=journal['path'],initialPublicationBarrierComplete=True,parentIdentity=journal['parent']['identity'],publicationSemantics=journal['parent']['publicationSemantics']);charge_retained_observation(journal_meta,value,limits,allocated,reserved,components,final)
  try:stdout,stderr,checker=capture_checksum_checker(ROOT,capture_state)
  except BaseException as error:
   buffers=capture_state.get('buffers',{});stdout=buffers.get('stdout',b'');stderr=buffers.get('stderr',b'');checker=capture_state.get('terminal',checker)
   checker['captureComplete']=False;checker['captureUnwoundError']={'type':type(error).__name__,'reason':str(error),'errno':getattr(error,'errno',None)}
   value['violations'].append('checker capture unwound; obtained evidence partial/unretained UNKNOWN')
  # The initial publication-ordered UNKNOWN marker exists before the checker. These raw
  # create-only commits precede every fallible postcapture mode/codec/output step.
  retained=retain_obtained_evidence(stdout,stderr,checker,value,ROOT,limits,allocated,reserved,components,final,journal)
  value['checkerTerminal']=checker;value['fullManifestActualShaCExit']=checker['actualReturnCode'];value['fullManifestRows']=stdout.count(b': OK')
  try:
   value['fullManifestCheckStderr']=stderr.decode('utf-8',errors='strict')
   value['fullManifestCheckStdoutEncoded']=encode_checksum_stdout(bytes(stdout))
   if checker['timedOut'] or checker['errors'] or not checker['captureComplete'] or checker['actualWait'] is None:raise RuntimeError('checker capture/timeout/wait incomplete or red')
   if not retained['fullObtainedStreamsRetained'] or not retained['actualTerminalRetained']:raise RuntimeError('obtained raw/actual terminal not committed; partial/unretained UNKNOWN')
   if type(checker['actualReturnCode']) is not int or checker['actualReturnCode']!=0:value['violations'].append('full executor immutable manifest actual nonzero/UNKNOWN')
   value['allRegularFiles0444']=all(not q.is_symlink() and stat.S_IMODE(q.stat().st_mode)==0o444 for q in ROOT.rglob('*') if q.is_file())
   if not value['allRegularFiles0444']:value['violations'].append('immutable modes red')
   value['passed']=not value['violations']
   # JSON serialization, text output and flush are inside this protection too.
   print(json.dumps(value,indent=2,allow_nan=False),flush=True)
   retention_journal_write(journal,b'RECEIPT_OUTPUT_COMMITTED_INNER_RESULT_PROVISIONAL_UNTIL_ACTUAL_WAIT_AND_ROOT_FINAL\n')
   code=0 if value['passed'] else 1
  except BaseException as error:
   value['passed']=False
   try:retain_checksum_failure(stdout,stderr,error,value,ROOT,limits,allocated,reserved,components,final,journal)
   except BaseException:
    try:retention_journal_write(journal,b'FALLBACK_OR_METADATA_ALLOCATION_FAILED_PARTIAL_UNRETAINED_UNKNOWN_RED\n')
    except BaseException:pass
   try:print(json.dumps(value,indent=2,allow_nan=False),flush=True)
   except BaseException:
    try:retention_journal_write(journal,b'RECEIPT_SERIALIZATION_OUTPUT_FAILED_UNKNOWN_RED\n')
    except BaseException:pass
   code=1
 except BaseException:
  # Even failure while attaching/hash/serializing retention metadata cannot
  # erase the pre-capture publication-ordered marker or overwrite obtained raw artifacts.
  try:retention_journal_write(journal,b'POSTCAPTURE_UNWOUND_PARTIAL_UNRETAINED_UNKNOWN_RED\n')
  except BaseException:pass
  code=1
 finally:
  close_errors=[]
  try:os.close(journal['fd'])
  except BaseException as error:
   code=1;close_errors.append({'phase':'journal-file-close','type':type(error).__name__,'reason':str(error),'errno':getattr(error,'errno',None)})
  try:close_parent(journal['parent'])
  except BaseException as error:
   code=1;close_errors.append({'phase':'publication-parent-close','type':type(error).__name__,'reason':str(error),'errno':getattr(error,'errno',None)})
  if close_errors:
   # Original stdout/raw/journal files are not reopened. A bounded actual
   # stderr diagnostic supplements actual nonzero; failure to emit stays RED.
   try:
    diagnostic=(json.dumps({'actualExecutorExit':1,'publicationCloseErrors':close_errors,'publicationStatus':'UNKNOWN/RED','originalRawPreserved':True},sort_keys=True,allow_nan=False)+'\n').encode('utf-8')
    assert len(diagnostic)<=4096 and os.write(2,diagnostic)==len(diagnostic)
   except BaseException:code=1
 return code

if __name__=='__main__':raise SystemExit(main())
