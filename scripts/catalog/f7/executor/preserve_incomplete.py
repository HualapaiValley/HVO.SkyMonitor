"""One create-only local preservation path if finalization itself is red. No remote call/replay."""
import json,os,pathlib,stat,subprocess
from capture import ROOT,save,digest,stamp,identity
from durable_publication import bind_parent,publish_bytes,close_parent

def main(error,terminal_transition=None):
 late=pathlib.Path(str(ROOT)+'.finalization-STOP.json')
 parent=bind_parent(late)
 try:
  body=(json.dumps({'utc':stamp(),'type':type(error).__name__,'reason':str(error),'errno':getattr(error,'errno',None),'terminalTransition':terminal_transition,'actualExecutorExit':1,'originalResultAndFIRSTUnchanged':True,'noRetry':True,'noFurtherRemoteCalls':True})+'\n').encode('utf-8')
  publish_bytes(parent,late,body)
 finally:close_parent(parent)
 if not (ROOT/'result.json').exists():
  b=json.loads((ROOT/'execution-binding.json').read_text());p=json.loads((pathlib.Path(b['sourcePacket'])/'candidate-pins.json').read_text())
  save('result.json',{'verdict':'INCOMPLETE / finalizer red / consumed','executorActualExit':1,'resourceLimits':p['resource'],'remoteFINAL':None,'firstRed':json.loads((ROOT/'external-STOP.json').read_text()) if (ROOT/'external-STOP.json').is_file() else str(error),'frozenRemoteOOM':0,'originalRemoteFIRSTPreserved':True,'noRetry':True})
 if not (ROOT/'SHA256SUMS').exists():
  files=sorted(q for q in ROOT.rglob('*') if q.is_file())
  for q in files:assert not q.is_symlink();q.chmod(0o444)
  with (ROOT/'SHA256SUMS').open('x') as f:
   for q in files:f.write(digest(q)+'  ./'+str(q.relative_to(ROOT))+'\n')
   f.flush();os.fsync(f.fileno())
  (ROOT/'SHA256SUMS').chmod(0o444)
 # Only preservation and an actual native final sample follow a finalizer red. Never repeat it.
 final=pathlib.Path(str(ROOT)+'.postseal.final.json')
 if not final.exists():
  with final.open('xb') as out,pathlib.Path(str(ROOT)+'.postseal.final.stderr').open('xb') as err:
   readfd,writefd=os.pipe();trampoline='import os,sys;fd=int(sys.argv[1]);assert os.read(fd,1)==b"1";os.close(fd);os.execvp(sys.argv[2],sys.argv[2:])'
   p=subprocess.Popen([os.sys.executable,'-B','-c',trampoline,str(readfd),os.sys.executable,'-B',str(ROOT/'external-afterseal.py'),str(ROOT)],stdout=out,stderr=err,pass_fds=(readfd,),start_new_session=True);os.close(readfd)
   i=identity(p.pid)
   with pathlib.Path(str(ROOT)+'.postseal.child-before-work.json').open('x') as f:json.dump({'actual':i,'barrier':'owned pipe, unreleased','noNamespaceMapping':True,'preservationOnly':True},f)
   assert i['ppid']==os.getpid() and i['uid']==os.getuid() and i['pgid']==p.pid and i['sid']==p.pid
   os.write(writefd,b'1');os.close(writefd)
   pid,status,usage=os.wait4(p.pid,0);rc=os.waitstatus_to_exitcode(status);p.returncode=rc
  wait=pathlib.Path(str(ROOT)+'.postseal.actual-wait.json')
  with wait.open('x') as f:json.dump({'utc':stamp(),'pid':pid,'rawWaitStatus':status,'actualReturnCode':rc,'WIFEXITED':os.WIFEXITED(status),'WEXITSTATUS':os.WEXITSTATUS(status) if os.WIFEXITED(status) else None,'WIFSIGNALED':os.WIFSIGNALED(status),'WTERMSIG':os.WTERMSIG(status) if os.WIFSIGNALED(status) else None,'wait4usage':{k:getattr(usage,k) for k in ['ru_utime','ru_stime','ru_maxrss','ru_inblock','ru_oublock','ru_nvcsw','ru_nivcsw']},'rawWait4NotOwnRSS':True,'ownSignalsSent':[],'preservationOnly':True},f)
 print(json.dumps({'FINITE_TESTEND':'INCOMPLETE / consumed','actualExecutorExit':1,'fullManifestSHA256':digest(ROOT/'SHA256SUMS'),'externalFinalReceipt':str(final),'finalizerStop':str(late),'noFurtherAction':True}),flush=True)
 return 1
