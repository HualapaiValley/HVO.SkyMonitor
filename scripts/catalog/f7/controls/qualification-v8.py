"""v8 private full-module controls; no native campaign or crash proof.
Original old62 and authored22 remain dated carry; this new entry runs only V7_CASES.
No actual kernel identity/wait/peak/release or source verdict is proven by these fakes.

Only a distinct bounded root allocation may invoke this script. Entire immutable
selected modules are compiled into fake namespaces; no selected function AST is
extracted. Selected logic never runs a real product, host/process-cohort,
subprocess, sent signal, checker or workload. The future actual adapter only
reads its own BS-F2 metadata and publishes bounded create-only output; actual
outer wait/resource/release and filesystem crash qualification remain separate.
The separately scoped adapter must capture its own actual wait/stdout/stderr,
BS-F2/resource/release/STOP/seal, including this adapter's errors. This source is
not a runtime allocation and does not establish scenario verification.
"""
import argparse,base64,builtins,gzip,hashlib,json,math,pathlib,stat,sys,types
HERE=pathlib.Path(__file__).resolve().parent
sys.path.insert(0,str(HERE))
from evidence_codec import encode_payload,SCHEMA
sys.path.pop(0)
PACKET=HERE.parent
SOURCES={'finalizer':PACKET/'executor/finalize_evidence.py','afterseal':PACKET/'executor/external-afterseal.py','execute':PACKET/'executor/execute.py','preserve_incomplete':PACKET/'executor/preserve_incomplete.py','publication':PACKET/'executor/durable_publication.py'}
DATED_V4_CASES_NOT_EXECUTED=[
 {'id':'terminal-early','route':'finalizer','clock':[10,12,12],'expected':0},
 {'id':'terminal-equality3880','route':'finalizer','clock':[3879,3880,3880],'expected':0},
 {'id':'terminal-3879plus2-lateRED','route':'finalizer','clock':[3879,3881,3881],'expected':1},
 {'id':'terminal-emission-crosses3880','route':'finalizer','clock':[3879,3879,3881],'expected':1},
 {'id':'terminal-codecSTOP-write-failure','route':'finalizer','clock':[10,12,12],'expected':1,'badReceipt':True,'failure':'codec-write'},
 {'id':'terminal-codecSTOP-fsync-failure','route':'finalizer','clock':[10,12,12],'expected':1,'badReceipt':True,'failure':'codec-fsync'},
 {'id':'terminal-lateSTOP-write-failure','route':'finalizer','clock':[3879,3881,3881],'expected':1,'failure':'late-write'},
 {'id':'terminal-actual-wrapper-red','route':'finalizer','clock':[10,12,12],'expected':1,'actualWrapper':1},
 {'id':'terminal-actual-wrapper-wait-failure','route':'finalizer','clock':[10,12,12],'expected':1,'failure':'wrapper-wait'},
 {'id':'checker-healthy-complete','route':'afterseal','expected':0,'checker':'normal'},
 {'id':'checker-timeout-full-obtained-binary','route':'afterseal','expected':1,'checker':'timeout'},
 {'id':'checker-timeout-terminal-UNKNOWN','route':'afterseal','expected':1,'checker':'wait-unknown'},
 {'id':'checker-capture-failure-retains-obtained','route':'afterseal','expected':1,'checker':'capture-error'},
 {'id':'checker-wait-failure-retains-obtained','route':'afterseal','expected':1,'checker':'wait-error'},
 {'id':'checker-partial-EOF-remainsRED','route':'afterseal','expected':1,'checker':'partial'},
 {'id':'checker-fallback-write-failure','route':'afterseal','expected':1,'checker':'timeout','failure':'fallback-write'},
 {'id':'checker-fallback-short-write','route':'afterseal','expected':1,'checker':'timeout','failure':'fallback-short'},
 {'id':'checker-fallback-fsync-failure','route':'afterseal','expected':1,'checker':'timeout','failure':'fallback-fsync'},
 {'id':'checker-fallback-lstat-failure','route':'afterseal','expected':1,'checker':'timeout','failure':'fallback-lstat'},
 {'id':'checker-fallback-existing-createonly','route':'afterseal','expected':1,'checker':'timeout','failure':'fallback-existing'},
 {'id':'checker-own-signal-failure','route':'afterseal','expected':1,'checker':'timeout','failure':'own-signal'},
 {'id':'checker-nonzero-actual-terminal','route':'afterseal','expected':1,'checker':'exit-red'},
]

def digest(raw):return hashlib.sha256(raw).hexdigest()
def encoded(raw):
 packed=gzip.compress(raw,compresslevel=9,mtime=0)
 return {'codec':'gzip-mtime0-base64-utf8-v1','rawBytes':len(raw),'rawSha256':digest(raw),'lineCount':raw.count(b'\n'),'okRows':raw.count(b': OK'),'packedBytes':len(packed),'packedSha256':digest(packed),'data':base64.b64encode(packed).decode()}

class MemoryFS:
 """A private finite fake file model, never patches the real filesystem."""
 def __init__(self,case):self.case=case;self.nodes={};self.log=[];self.fds={};self.nextfd=300
 def put(self,name,raw=b'',directory=False,blocks=None):
  name=str(name);p=pathlib.PurePosixPath(name)
  if str(p.parent)!=name and str(p.parent) not in self.nodes:self.put(p.parent,directory=True)
  self.nodes[name]={'raw':raw,'directory':directory,'blocks':(0 if directory or not raw else ((len(raw)+4095)//4096)*8) if blocks is None else blocks,'mode':0o444,'inode':len(self.nodes)+1}
 def Path(self,name):
  if isinstance(name,MemPath):return name
  return MemPath(self,str(name))
 def json(self,name,value):self.put(name,(json.dumps(value)+'\n').encode())
 def failure(self,kind,path):
  failure=self.case.get('failure','')
  marker='fallback' if 'check-output-fallback' in str(path) else 'codec' if 'codec-STOP' in str(path) else 'late' if 'late-STOP' in str(path) else ''
  return failure==marker+'-'+kind
 def fsync(self,fd):
  path=self.fds[fd];self.log.append(['fsync',path])
  if self.failure('fsync',path):raise OSError(5,'fake explicit fsync failure',path)

class MemPath:
 def __init__(self,fs,name):self.fs=fs;self.key=str(pathlib.PurePosixPath(name))
 @property
 def name(self):return pathlib.PurePosixPath(self.key).name
 def __str__(self):return self.key
 def __truediv__(self,n):return self.fs.Path(pathlib.PurePosixPath(self.key)/str(n))
 def __lt__(self,other):return self.key<str(other)
 @property
 def parent(self):return self.fs.Path(pathlib.PurePosixPath(self.key).parent)
 def relative_to(self,root):return pathlib.PurePosixPath(self.key).relative_to(str(root))
 def is_file(self):return self.key in self.fs.nodes and not self.fs.nodes[self.key]['directory']
 def is_dir(self):return self.key in self.fs.nodes and self.fs.nodes[self.key]['directory']
 def is_symlink(self):return False
 def exists(self):return self.key in self.fs.nodes
 def read_bytes(self):
  if not self.exists():raise FileNotFoundError(2,'fake absent',self.key)
  return self.fs.nodes[self.key]['raw']
 def read_text(self):return self.read_bytes().decode('utf-8')
 def chmod(self,mode):self.fs.nodes[self.key]['mode']=mode;self.fs.log.append(['chmod',self.key,mode])
 def rglob(self,pattern):
  assert pattern=='*'
  prefix=self.key.rstrip('/')+'/'
  return [self.fs.Path(n) for n in sorted(self.fs.nodes) if n.startswith(prefix)]
 def stat(self):
  if not self.exists():raise FileNotFoundError(2,'fake absent',self.key)
  if self.fs.failure('lstat',self.key):raise OSError(5,'fake explicit fallback lstat failure',self.key)
  n=self.fs.nodes[self.key]
  return types.SimpleNamespace(st_mode=(stat.S_IFDIR if n['directory'] else stat.S_IFREG)|n['mode'],st_size=len(n['raw']),st_blocks=n['blocks'],st_uid=1000,st_ino=1179670 if self.key=='/tmp/hvo-520-heavy.lock' else n['inode'],st_dev=1,st_nlink=1)
 def lstat(self):return self.stat()
 def open(self,mode):
  assert mode in ('x','xb','a','ab')
  if 'x' in mode and self.exists():raise FileExistsError(17,'fake create-only existing',self.key)
  if 'x' in mode or 'a' in mode and not self.exists():self.fs.put(self.key)
  self.fs.log.append(['open',self.key,mode]);self.fs.nextfd+=1;fd=self.fs.nextfd;self.fs.fds[fd]=self.key
  path=self
  class Stream:
   def __enter__(self):return self
   def __exit__(self,*args):self.close();return False
   def close(self):path.fs.log.append(['close-stream',path.key])
   def fileno(self):return fd
   def flush(self):path.fs.log.append(['flush',path.key])
   def write(self,raw):
    body=raw.encode() if isinstance(raw,str) else raw
    path.fs.log.append(['write',path.key,len(body)])
    if path.fs.failure('write',path.key):
     body=body[:17];path.fs.nodes[path.key]['raw']+=body;path.fs.nodes[path.key]['blocks']=8;raise OSError(28,'fake explicit partial write failure',path.key)
    if path.fs.failure('short',path.key):body=body[:17]
    path.fs.nodes[path.key]['raw']+=body;path.fs.nodes[path.key]['blocks']=((len(path.fs.nodes[path.key]['raw'])+4095)//4096)*8
    return len(body)
  return Stream()

class Clock:
 def __init__(self,values=None):self.values=list(values or []);self.current=0
 def monotonic(self):
  if self.values:self.current=self.values.pop(0)
  return self.current

class Pipe:
 def __init__(self,fd):self.fd=fd;self.closed=False
 def fileno(self):return self.fd
 def close(self):self.closed=True

class FakeRuntime:
 def __init__(self,case):
  self.case=case;self.fs=MemoryFS(case);self.clock=Clock(case.get('clock'));self.output=[];self.calls=[];self.signals=[];self.obtained={'stdout':bytearray(),'stderr':bytearray()};self.readcounts={101:0,102:0};self.did_capture_error=False;self.pid=2100;self.child=None;self.reaped=False;self.selected_rounds=0
  if case.get('checker')=='normal':self.raw={'stdout':b'one: OK\n','stderr':b''}
  elif case.get('checker')=='exit-red':self.raw={'stdout':b'one: FAILED\n','stderr':b'actual nonzero\n'}
  else:self.raw={'stdout':b'prefix: OK\n\xff\x00stdout-unique-tail\n','stderr':b'\x00\xfechecker-error-prefix\nunique-stderr-tail'}
  self.remaining={101:[self.raw['stdout'][:5],self.raw['stdout'][5:],b''],102:[self.raw['stderr'][:3],self.raw['stderr'][3:],b'']}
  self.pathlib=types.SimpleNamespace(Path=self.fs.Path,PurePosixPath=pathlib.PurePosixPath)
  self.os=types.SimpleNamespace(getpid=lambda:2000,getuid=lambda:1000,pipe=lambda:(90,91),close=self.close,write=self.write,read=self.read,set_blocking=lambda *a:None,pidfd_open=self.pidfd_open,wait4=self.wait4,waitstatus_to_exitcode=lambda status:status,WIFEXITED=lambda status:status>=0,WEXITSTATUS=lambda status:status if status>=0 else None,WIFSIGNALED=lambda status:status<0,WTERMSIG=lambda status:-status if status<0 else None,WNOHANG=1,fsync=self.fs.fsync,statvfs=lambda root:types.SimpleNamespace(f_bavail=2**30,f_frsize=4096),path=types.SimpleNamespace(lexists=lambda path:str(path) in self.fs.nodes),sys=types.SimpleNamespace(executable='/fake/python3'))
  self.subprocess=types.SimpleNamespace(PIPE=-1,Popen=self.popen,run=self.run,check_output=self.check_output)
  self.selectors=types.SimpleNamespace(DefaultSelector=self.selector,EVENT_READ=1)
  self.signal=types.SimpleNamespace(SIGKILL=9,pidfd_send_signal=self.send_signal)
  self.sys=types.SimpleNamespace(argv=['/fake/selected.py','/f/executor'],executable='/fake/python3')
  self.resource=types.SimpleNamespace(RUSAGE_SELF=0,getrusage=lambda unused:self.usage())
  self.capture=types.SimpleNamespace(ROOT=self.fs.Path('/f/executor'),save=self.save,digest=lambda p:digest(p.read_bytes()),boundary=self.boundary,stamp=lambda:'FAKE-UTC-NOT-OBSERVED',identity=lambda pid:{'pid':pid,'ppid':2000,'uid':1000,'pgid':pid,'sid':pid,'birth':17})
 def usage(self):return types.SimpleNamespace(**{k:0 for k in ['ru_utime','ru_stime','ru_maxrss','ru_inblock','ru_oublock','ru_nvcsw','ru_nivcsw']})
 def save(self,name,value):
  body=value if type(value) is bytes else (json.dumps(value)+'\n').encode()
  with (self.capture.ROOT/name).open('xb') as out:assert out.write(body)==len(body);out.flush();self.fs.fsync(out.fileno())
 def boundary(self,role):
  value={'executorAllocatedBytes':8192};self.save(role+'.local-boundary.json',value);return value
 def close(self,fd):self.calls.append(['close-fd',fd])
 def write(self,fd,body):self.calls.append(['barrier-write',fd,body.hex()]);return len(body)
 def pidfd_open(self,pid,flags):assert pid==self.pid and flags==0;self.calls.append(['pidfd',pid]);return 92
 def send_signal(self,pidfd,number,info,flags):
  assert pidfd==92 and number==9 and info is None and flags==0
  self.signals.append({'pid':self.pid,'signal':9,'attempted':True})
  if self.case.get('failure')=='own-signal':raise OSError(1,'fake own signal rejected')
  self.signals[-1]['sent']=True
 def read(self,fd,count):
  assert count==65536 and fd in (101,102);self.readcounts[fd]+=1;name='stdout' if fd==101 else 'stderr'
  if self.case.get('checker')=='capture-error' and fd==102 and not self.did_capture_error:self.did_capture_error=True;raise OSError(5,'fake capture error with stdout already obtained')
  if self.case.get('checker')=='partial' and fd==102 and self.remaining[fd]==[b'']:raise BlockingIOError(11,'fake EOF not yet observed')
  chunks=self.remaining[fd]
  if not chunks:return b''
  raw=chunks.pop(0)
  if not raw and chunks:return self.read(fd,count)
  self.obtained[name].extend(raw);return raw
 def wait4(self,pid,flags):
  assert pid==self.pid;self.calls.append(['wait4',pid,flags])
  if self.case.get('failure')=='wrapper-wait' or self.case.get('checker')=='wait-error':raise OSError(5,'fake actual wait failure')
  if self.case.get('checker')=='wait-unknown' or self.case.get('failure')=='own-signal':return 0,0,self.usage()
  if self.case['route']=='finalizer':status=self.case.get('actualWrapper',0)
  elif self.case.get('checker')=='exit-red':status=1
  elif self.signals:status=-9
  elif self.case.get('checker') in ('timeout','partial'):return 0,0,self.usage()
  else:status=0
  self.reaped=True;return pid,status,self.usage()
 def popen(self,argv,**kw):
  self.calls.append(['Popen',argv]);assert self.child is None,'no retry or second child'
  self.child=types.SimpleNamespace(pid=self.pid,stdout=Pipe(101),stderr=Pipe(102),returncode=None)
  if self.case['route']=='finalizer':
   count=len(self.fs.nodes['/f/executor/SHA256SUMS']['raw'].splitlines())
   raw=b''.join((str(i)+': OK\n').encode() for i in range(count))
   receipt={'passed':True,'fullManifestActualShaCExit':0,'fullManifestRows':count,'fullManifestCheckStdoutEncoded':encoded(raw)}
   if self.case.get('badReceipt'):receipt['fullManifestCheckStdoutEncoded']['rawSha256']='0'*64
   kw['stdout'].write(json.dumps(receipt).encode());kw['stderr'].write(b'')
  return self.child
 def selector(self):
  runtime=self
  class Selector:
   def __init__(self):self.keys={}
   def register(self,stream,events,name):self.keys[stream.fileno()]=types.SimpleNamespace(fileobj=stream,data=name)
   def unregister(self,stream):del self.keys[stream.fileno()]
   def select(self,timeout):
    runtime.selected_rounds+=1
    if timeout:
     runtime.clock.current+=0.05
     if runtime.case.get('checker') in ('timeout','wait-unknown','partial') and runtime.selected_rounds==1:runtime.clock.current=31
    return [(v,1) for v in list(self.keys.values())]
   def close(self):pass
  return Selector()
 def run(self,argv,**kw):
  assert argv==['sha256sum','-c','SHA256SUMS'];self.calls.append(['parent-check',argv])
  count=len(self.fs.nodes['/f/executor/SHA256SUMS']['raw'].splitlines());return types.SimpleNamespace(returncode=0,stdout=''.join(str(i)+': OK\n' for i in range(count)),stderr='')
 def check_output(self,argv,**kw):
  self.calls.append(['metadata',argv])
  if argv[0]=='du':return '0 /f/executor\n'
  if argv[0]=='hostname':return 'f7-fixture.invalid\n'
  if argv[0]=='df':return 'fake df header\nfake 1099511627776 0 1099511627776 0% /\n'
  if argv[0]=='/usr/bin/date':return 'FAKE-UTC-NOT-OBSERVED\n'
  raise AssertionError('unexpected actual-route metadata call '+repr(argv))
 def load_selected(self,route):
  import zlib
  modules={'base64':base64,'gzip':gzip,'hashlib':hashlib,'json':json,'stat':stat,'zlib':zlib,'os':self.os,'pathlib':self.pathlib,'subprocess':self.subprocess,'time':self.clock,'selectors':self.selectors,'signal':self.signal,'sys':self.sys,'resource':self.resource,'capture':self.capture,'errno':types.SimpleNamespace(ENOENT=2)}
  def fake_import(name,globals=None,locals=None,fromlist=(),level=0):
   assert level==0 and name in modules,'real/import escape '+name
   return modules[name]
  namespace={'__name__':'future_fake_selected_module','__builtins__':dict(vars(builtins),__import__=fake_import,print=lambda *args,**kw:self.output.append(' '.join(map(str,args))))}
  # Entire selected source, including main and the checker/capture/retention path.
  exec(compile(SOURCES[route].read_bytes(),str(SOURCES[route]),'exec'),namespace)
  return namespace

def seed_runtime(rt):
 fs=rt.fs;root='/f/executor';packet='/f/source'
 limits=json.loads((PACKET/'source/candidate-pins.json').read_bytes())['resource']
 components={k:0 for k in limits['componentCeilingsBytes']}
 final={'utc':'FAKE-UTC-NOT-OBSERVED','availableBytes':2**40,'memAvailableBytes':2**36,'oomKill':0,'workspaceAllocatedBytes':0,'componentsBytes':components,'nativeExecutorAllocatedBytes':0,'passed':True,'violations':[],'stageSourceHashes':{},'recordedWrapperPhysicalObservations':[]}
 fs.json(root+'/execution-binding.json',{'sourcePacket':packet})
 fs.json(packet+'/candidate-pins.json',dict(resource=limits,head='h'*40,tree='t'*40,base='b'*40))
 fs.json(root+'/executor-state.json',{'firstRed':None,'stageStarted':True,'nativeStarted':True,'nativeTerminalAndFIRSTBound':True,'releaseAttempts':1})
 fs.json(root+'/allocation.json',{'allocationId':'FAKE-INTEGRATION-NOT-ALLOCATED'})
 fs.json(root+'/external-final.stdout',final)
 for name in ['native.actual-wait.json','release.actual-wait.json','stage.actual-wait.json','external-final.actual-wait.json']:fs.json(root+'/'+name,{'actualReturnCode':0})
 fs.json(root+'/release.stdout',{'firstHashesUnchanged':True,'originalRootReverificationRc':0,'completeGroupOrSessionScan':{'complete':True,'unknowns':[]},'recordedIdentityUnknowns':[],'residualGroupOrSessionMembers':[],'lockAndWaiterLines':[],'allRecordedIdentities':[]})
 fs.json(root+'/finite-phase-start.json',{'monotonic':0})
 commands=json.loads((PACKET/'source/commands.json').read_bytes());fs.json(packet+'/commands.json',commands)
 raw=root+'/captured-rawrecords/1186-b60-f7-accounting-receipt-v7-e2d998ed/records'
 fs.put(raw+'/exit.code',b'0\n');fs.put(raw+'/terminal.txt',b'exact original fake FIRST terminal\n')
 for step in commands['orderedSteps']:fs.put(raw+'/'+step['id']+'.observer.actual-exit.code',b'0\n')
 files=sorted(n for n in fs.nodes if n.startswith(raw+'/') and not fs.nodes[n]['directory'])
 manifest=b''.join((digest(fs.nodes[n]['raw'])+'  '+n[len(raw)+1:]+'\n').encode() for n in files);fs.put(raw+'/SHA256SUMS',manifest)
 first={n:digest(fs.nodes[raw+'/'+n]['raw']) for n in ['SHA256SUMS','exit.code','terminal.txt']}
 fs.json(raw+'.acceptance.txt',{'entryCount':len(files),'firstHashes':first,'workloadExit':0,'acceptanceExit':0,'firstVerificationRc':0})
 fs.json(root+'/local-baseline.json',{'oomKill':0})
 if rt.case['route']=='afterseal':
  fs.json(root+'/result.json',{'resourceLimits':limits,'remoteFINAL':final})
  fs.put(root+'/SHA256SUMS',b'original immutable fake manifest\n')
  fields=['R']+['0']*19;fields[1]='2000';fields[2]=fields[3]=str(rt.pid);fields[19]='17'
  fs.put('/proc/self/stat',('2000 (fake self) '+' '.join(fields)).encode());fs.put('/proc/self/status',b'VmRSS: 2048 kB\n')
  fs.put('/proc/'+str(rt.pid)+'/stat',(str(rt.pid)+' (fake checker) '+' '.join(fields)).encode())
  fs.put('/proc/meminfo',b'MemAvailable: 67108864 kB\n');fs.put('/proc/vmstat',b'oom_kill 0\n');fs.put('/proc/locks',b'');fs.put('/tmp/hvo-520-heavy.lock',b'')
  if rt.case.get('failure')=='fallback-existing':fs.put(root+'.postseal.check-output-fallback.json',b'original existing fallback stays exact')
 return raw,first

# Each name is a new source-bound finite scenario, never a claimed outcome.
V5_CASES=[
 {'id':'v5-execute-signal-after-state-snapshot','route':'execute','inject':'snapshot','expected':143},
 {'id':'v5-execute-signal-before-first-seal','route':'execute','inject':'result','expected':143},
 {'id':'v5-execute-signal-during-terminal-print','route':'execute','inject':'terminal-print','expected':143},
 {'id':'v5-execute-signal-after-finalizer-at-outer-exit','route':'execute','inject':'outer-exit','expected':143},
 {'id':'v5-execute-late-signal-write-failure','route':'execute','inject':'snapshot','failure':'signal-write','expected':143},
 {'id':'v5-normal-complete-owned-barrier-and-retention','route':'afterseal','checker':'normal','expected':0},
 {'id':'v5-mode-EIO-after-complete-capture','route':'afterseal','checker':'normal','failure':'mode-EIO','expected':1},
 {'id':'v5-fallback-JSON-MemoryError','route':'afterseal','checker':'timeout','failure':'fallback-JSON-memory','expected':1},
 {'id':'v5-fallback-base64-MemoryError','route':'afterseal','checker':'timeout','failure':'fallback-base64-memory','expected':1},
 {'id':'v5-checker-terminal-JSON-MemoryError','route':'afterseal','checker':'normal','failure':'terminal-JSON-memory','expected':1},
 {'id':'v5-output-JSON-MemoryError','route':'afterseal','checker':'normal','failure':'output-JSON-memory','expected':1},
 {'id':'v5-print-failure-after-full-raw','route':'afterseal','checker':'normal','failure':'print','expected':1},
 {'id':'v5-print-flush-failure-after-full-raw','route':'afterseal','checker':'normal','failure':'print-flush','expected':1},
 {'id':'v5-raw-stdout-write-MemoryError','route':'afterseal','checker':'normal','failure':'stdout-memory','expected':1},
 {'id':'v5-raw-stderr-FSIZE-partial','route':'afterseal','checker':'timeout','failure':'stderr-FSIZE','expected':1},
 {'id':'v5-fallback-EIO-existing-partial-no-zero','route':'afterseal','checker':'timeout','failure':'fallback-EIO','expected':1},
 {'id':'v5-fallback-EACCES-existing-partial-no-zero','route':'afterseal','checker':'timeout','failure':'fallback-EACCES','expected':1},
 {'id':'v5-fallback-ENOTDIR-unknown-no-zero','route':'afterseal','checker':'timeout','failure':'fallback-ENOTDIR','expected':1},
 {'id':'v5-fallback-ENOENT-with-verified-parent','route':'afterseal','checker':'timeout','failure':'fallback-ENOENT','expected':1},
 {'id':'v5-journal-fsync-failure-checker-UNRUN','route':'afterseal','checker':'normal','failure':'journal-fsync','expected':1},
 {'id':'v5-journal-short-write-checker-UNRUN','route':'afterseal','checker':'normal','failure':'journal-short','expected':1},
 {'id':'v5-owned-UID-mismatch','route':'afterseal','checker':'normal','failure':'identity-UID','expected':1},
 {'id':'v5-owned-PGID-mismatch','route':'afterseal','checker':'normal','failure':'identity-PGID','expected':1},
 {'id':'v5-owned-cmdline-mismatch','route':'afterseal','checker':'normal','failure':'identity-argv','expected':1},
 {'id':'v5-owned-exe-mismatch','route':'afterseal','checker':'normal','failure':'identity-exe','expected':1},
 {'id':'v5-owned-root-EACCES','route':'afterseal','checker':'normal','failure':'identity-root-read','expected':1},
 {'id':'v5-owned-stat-EIO','route':'afterseal','checker':'normal','failure':'identity-stat-read','expected':1},
 {'id':'v5-owned-status-EIO','route':'afterseal','checker':'normal','failure':'identity-status-read','expected':1},
 {'id':'v5-owned-cmdline-EIO','route':'afterseal','checker':'normal','failure':'identity-cmdline-read','expected':1},
 {'id':'v5-owned-exe-read-EIO','route':'afterseal','checker':'normal','failure':'identity-exe-read','expected':1},
 {'id':'v5-owned-pidfd-open-failure','route':'afterseal','checker':'normal','failure':'pidfd-open','expected':1},
 {'id':'v5-owned-pidfd-open-metadata-race','route':'afterseal','checker':'normal','failure':'pidfd-race','expected':1},
 {'id':'v5-private-Popen-setup-failure','route':'afterseal','checker':'normal','failure':'Popen','expected':1},
 {'id':'v5-selector-register-failure','route':'afterseal','checker':'normal','failure':'selector-register','expected':1},
 {'id':'v5-selector-select-drain-failure','route':'afterseal','checker':'normal','failure':'selector-select','expected':1},
 {'id':'v5-stream-read-MemoryError','route':'afterseal','checker':'normal','failure':'read-memory','expected':1},
 {'id':'v5-stream-bound-crossing-full-obtained','route':'afterseal','checker':'normal','failure':'stream-overflow','expected':1},
 {'id':'v5-owned-stream-close-failure','route':'afterseal','checker':'normal','failure':'stream-close','expected':1},
 {'id':'v5-actual-wait-failure-and-journal','route':'afterseal','checker':'wait-error','expected':1},
 {'id':'v5-wait-UNKNOWN-no-release-credit','route':'afterseal','checker':'wait-unknown','expected':1},
 {'id':'v5-partial-EOF-retained-red','route':'afterseal','checker':'partial','expected':1},
 {'id':'v5-native-disk-reserve-red','route':'afterseal','checker':'normal','failure':'disk','expected':1},
 {'id':'v5-native-RSS-cap-red','route':'afterseal','checker':'normal','failure':'RSS','expected':1},
 {'id':'v5-common-retention-component-cap-red','route':'afterseal','checker':'normal','failure':'component','expected':1},
]

class V5Path(MemPath):
 def mkdir(self,parents=False,exist_ok=False):
  if self.exists() and not exist_ok:raise FileExistsError(17,'fake create-only dir exists',self.key)
  self.fs.put(self.key,directory=True)
 def resolve(self):return self
 def read_bytes(self):
  failure=self.fs.case.get('failure');tail=self.key.split('/')[-1]
  if '/proc/2100/' in self.key and failure=='identity-'+tail+'-read':raise OSError(5,'fake complete identity read failure',self.key)
  raw=super().read_bytes()
  if tail=='status' and '/proc/2100/' in self.key and failure=='identity-UID':raw=raw.replace(b'1000',b'1001')
  if tail=='cmdline' and failure=='identity-argv':raw=b'foreign\0argv\0'
  return raw
 def stat(self):
  failure=self.fs.case.get('failure')
  if self.key=='/f/executor/result.json' and failure=='mode-EIO':raise OSError(5,'fake successful capture then final mode stat EIO',self.key)
  if self.key=='/proc/2100' and failure=='identity-root-read':raise PermissionError(13,'fake owned root EACCES',self.key)
  if self.key=='/proc/2100/exe':
   value=self.fs.Path('/fake/python3').stat()
   if failure=='identity-exe':value.st_ino+=1
   return value
  value=super().stat()
  if self.key=='/proc/2100' and failure=='pidfd-race' and self.fs.runtime.pidfd_seen:value.st_ino+=1
  return value
 def open(self,mode):
  failure=self.fs.case.get('failure')
  if mode=='rb':
   import io
   return io.BytesIO(self.read_bytes())
  if self.key.endswith('.postseal.check-output-fallback.json') and failure=='fallback-ENOENT':raise OSError(28,'fake create failed before file publication',self.key)
  stream=super().open(mode);write=stream.write;path=self
  def changed(raw):
   if path.key.endswith('.postseal.check-stdout.raw') and failure=='stdout-memory':raise MemoryError('fake raw publication allocation failure')
   if path.key.endswith('.postseal.check-stderr.raw') and failure=='stderr-FSIZE':
    write(raw[:17]);raise OSError(27,'fake FSIZE partial raw write',path.key)
   if path.key.endswith('.postseal.check-output-fallback.json') and failure in ('fallback-EIO','fallback-EACCES','fallback-ENOTDIR'):
    write(raw[:17]);raise OSError(28,'fake partial fallback publication failure',path.key)
   return write(raw)
  stream.write=changed;return stream

class V5FS(MemoryFS):
 def Path(self,name):return name if isinstance(name,V5Path) else V5Path(self,str(name))
 def fsync(self,fd):
  if self.case.get('failure')=='journal-fsync' and self.fds[fd].endswith('.postseal.check-retention.journal'):raise OSError(5,'fake initial journal fsync failure')
  super().fsync(fd)

class V5Runtime(FakeRuntime):
 def __init__(self,case):
  super().__init__(case);self.fs=V5FS(case);self.fs.runtime=self;self.pidfd_seen=False;self.handlers={};self.injected=False;self.modules={};self.clock=Clock([10] if case['route']=='execute' else None)
  self.pathlib=types.SimpleNamespace(Path=self.fs.Path,PurePosixPath=pathlib.PurePosixPath)
  self.capture.ROOT=self.fs.Path('/f/executor');self.capture.invoke=self.invoke;self.capture.save=self.save
  self.sys.exit=self.exit
  self.os.O_WRONLY=1;self.os.O_CREAT=64;self.os.O_EXCL=128;self.os.O_CLOEXEC=524288;self.os.O_NOFOLLOW=131072
  self.os.open=self.open_fd;self.os.stat=lambda name:self.fs.Path(name).stat();self.os.readlink=self.readlink;self.os.fsync=self.fs.fsync
  self.os.path.realpath=lambda name:str(name);self.os.path.lexists=lambda path:(_ for _ in ()).throw(AssertionError('v5 must never use lexists'))
  self.signal.SIGTERM=15;self.signal.SIGINT=2;self.signal.SIGHUP=1;self.signal.signal=lambda number,handler:self.handlers.update({number:handler})
  if case.get('failure')=='disk':self.os.statvfs=lambda root:types.SimpleNamespace(f_bavail=1,f_frsize=4096)
 def inject(self,phase):
  if not self.injected and self.case.get('inject')==phase:
   self.injected=True;self.handlers[15](15,None)
 def save(self,name,value):
  super().save(name,value)
  if name=='executor-state.json':self.inject('snapshot')
  if name=='result.json':self.inject('result')
 def exit(self,code):self.inject('outer-exit');raise SystemExit(code)
 def print_record(self,*args,**kw):
  text=' '.join(map(str,args));self.inject('terminal-print' if '"FINITE_TESTEND"' in text else 'unselected')
  if self.case.get('failure') in ('print','print-flush'):raise OSError(5,'fake receipt '+self.case['failure'])
  self.output.append(text)
 def open_fd(self,name,flags,mode):
  assert flags & self.os.O_EXCL and flags & self.os.O_NOFOLLOW
  if str(name) in self.fs.nodes:raise FileExistsError(17,'fake owned create-only fd exists',str(name))
  self.fs.put(str(name));self.fs.nextfd+=1;self.fs.fds[self.fs.nextfd]=str(name);return self.fs.nextfd
 def write(self,fd,body):
  if fd==91:return super().write(fd,body)
  path=self.fs.fds[fd]
  if path.endswith('.late-signals.jsonl') and self.case.get('failure')=='signal-write':self.fs.nodes[path]['raw']+=body[:1];self.fs.nodes[path]['blocks']=8;raise OSError(28,'fake partial late signal write',path)
  if path.endswith('.postseal.check-retention.journal') and self.case.get('failure')=='journal-short':body=body[:17]
  self.fs.nodes[path]['raw']+=body;self.fs.nodes[path]['blocks']=((len(self.fs.nodes[path]['raw'])+4095)//4096)*8;return len(body)
 def readlink(self,name):
  if self.case.get('failure')=='identity-exe-read':raise OSError(5,'fake exe link read EIO',str(name))
  assert str(name)=='/proc/2100/exe';return '/fake/python3'
 def pidfd_open(self,pid,flags):
  if self.case.get('failure')=='pidfd-open':raise OSError(24,'fake pidfd open failure')
  self.pidfd_seen=True;return super().pidfd_open(pid,flags)
 def popen(self,argv,**kw):
  if self.case.get('failure')=='Popen':raise OSError(12,'fake private Popen setup failure')
  savedroute=self.case['route']
  if savedroute=='execute':self.case['route']='finalizer'
  try:child=super().popen(argv,**kw)
  finally:self.case['route']=savedroute
  if savedroute!='execute':
   fields=['R']+['0']*19;fields[1]='2000';fields[2]=fields[3]='2100';fields[19]='17'
   if self.case.get('failure')=='identity-PGID':fields[2]='2999'
   self.fs.put('/proc/2100/stat',('2100 (fake checker) '+' '.join(fields)).encode());self.fs.put('/proc/2100/status',b'Uid:\t1000\t1000\t1000\t1000\nVmRSS: 2048 kB\n');self.fs.put('/proc/2100/cmdline',b'\0'.join(x.encode() for x in argv)+b'\0');self.fs.put('/proc/2100/exe',b'')
   if self.case.get('failure')=='stream-close':child.stdout.close=lambda:(_ for _ in ()).throw(OSError(5,'fake owned stream close failure'))
   if self.case.get('failure')=='stream-overflow':self.raw['stdout']=b'A'*1114112;self.remaining[101]=[self.raw['stdout'][i:i+65536] for i in range(0,len(self.raw['stdout']),65536)]+[b'']
  return child
 def read(self,fd,count):
  if self.case.get('failure')=='read-memory':raise MemoryError('fake os.read allocation failure')
  return super().read(fd,count)
 def wait4(self,pid,flags):
  if self.case['route']=='execute':return pid,0,self.usage()
  return super().wait4(pid,flags)
 def selector(self):
  selector=super().selector();register=selector.register;select=selector.select
  def changed_register(*args):
   if self.case.get('failure')=='selector-register':raise OSError(12,'fake selector register failure')
   return register(*args)
  def changed_select(timeout):
   if self.case.get('failure')=='selector-select':raise OSError(5,'fake selector and bounded drain failure')
   return select(timeout)
  selector.register=changed_register;selector.select=changed_select;return selector
 def load_selected(self,route):
  import errno,zlib
  if route in self.modules:return self.modules[route]
  def dumps(value,*a,**kw):
   failure=self.case.get('failure')
   if failure=='fallback-JSON-memory' and isinstance(value,dict) and 'stdoutBase64' in value:raise MemoryError('fake fallback JSON allocation')
   if failure=='terminal-JSON-memory' and isinstance(value,dict) and 'argv' in value:raise MemoryError('fake actual checker terminal JSON allocation')
   if failure=='output-JSON-memory' and isinstance(value,dict) and 'BS-F2' in value:raise MemoryError('fake receipt JSON allocation')
   return json.dumps(value,*a,**kw)
  def b64(raw):
   if self.case.get('failure')=='fallback-base64-memory' and bytes(raw)==self.raw['stdout']:raise MemoryError('fake fallback base64 allocation')
   return base64.b64encode(raw)
  pure={'base64':types.SimpleNamespace(b64encode=b64,b64decode=base64.b64decode,binascii=base64.binascii),'gzip':gzip,'hashlib':hashlib,'json':types.SimpleNamespace(dumps=dumps,dump=json.dump,loads=json.loads),'stat':stat,'zlib':zlib,'errno':errno,'os':self.os,'pathlib':self.pathlib,'subprocess':self.subprocess,'time':self.clock,'selectors':self.selectors,'signal':self.signal,'sys':self.sys,'resource':self.resource,'capture':self.capture,'tarfile':types.SimpleNamespace(open=lambda *a,**kw:self.archive())}
  def fake_import(name,globals=None,locals=None,fromlist=(),level=0):
   assert level==0
   if name in ('finalize_evidence','preserve_incomplete'):return types.SimpleNamespace(**self.load_selected({'finalize_evidence':'finalizer','preserve_incomplete':'preserve_incomplete'}[name]))
   if name=='durable_publication':return types.SimpleNamespace(**self.load_selected('publication'))
   assert name in pure,'real/import escape '+name
   return pure[name]
  namespace={'__name__':'v5_full_selected_fake','__builtins__':dict(vars(builtins),__import__=fake_import,print=self.print_record)}
  self.modules[route]=namespace
  exec(compile(SOURCES[route].read_bytes(),str(SOURCES[route]),'exec'),namespace)
  return namespace
 def archive(self):
  import io
  runtime=self
  class Archive:
   def __enter__(self):return self
   def __exit__(self,*args):return False
   def __iter__(self):return iter([types.SimpleNamespace(name=n,size=len(body),isfile=lambda:True) for n,body in runtime.archive_files.items()])
   def extractfile(self,member):return io.BytesIO(runtime.archive_files[member.name])
  return Archive()
 def invoke(self,name,argv,stdin=None):
  phase=name.removesuffix('-executor-wrapper');root='/f/executor';fs=self.fs
  fs.json(root+'/'+phase+'.invoked.json',{'fakeOnly':True});fs.json(root+'/'+phase+'.actual-wait.json',{'actualReturnCode':0})
  output=self.phase_outputs.get(phase,{});fs.put(root+'/'+phase+'.stdout',output if isinstance(output,bytes) else (json.dumps(output)+'\n').encode());fs.put(root+'/'+phase+'.stderr',self.phase_stderr.get(phase,b''));return 0


def seed_v5(rt):
 raw,first=seed_runtime(rt);fs=rt.fs;root='/f/executor'
 fs.put('/fake/python3',b'fake pinned Python executable',blocks=8)
 if rt.case.get('failure')=='RSS':fs.put('/proc/self/status',b'VmRSS: 524288 kB\n')
 if rt.case.get('failure')=='component':
  v=json.loads(fs.nodes[root+'/result.json']['raw']);v['remoteFINAL']['componentsBytes']['retainedPacketAndBundle']=v['resourceLimits']['componentCeilingsBytes']['retainedPacketAndBundle'];fs.json(root+'/result.json',v)
 if rt.case['route']=='execute':
  fs.json(root+'/execution-binding.json',{'sourcePacket':'/f/source','stageBinding':'/f/stagebinding'})
  expected=json.loads((PACKET/'stage-binding/expected-stage-members.json').read_bytes());fs.json('/f/stagebinding/expected-stage-members.json',expected)
  for name in ['inputs','route-config','admission','allocation-copy','stage','poststage','inputs-before-work','prework','native','postnative','release','rawrecords','external-final']:fs.json(root+'/'+name+'.spec.json',{'argv':['/fake/exact-bound-spec',name]})
  acceptance=json.loads(fs.nodes[raw+'.acceptance.txt']['raw']);acceptance.update(manifestSelfExcluded=True,firstEvidencePreserved=True,noRegeneration=True);fs.json(raw+'.acceptance.txt',acceptance);terminal={'actualExitCode':0,'signalsReceived':[],'signalsSent':[],'phase':'complete-source-copy-only','manifestCheckRc':0,'manifestCheckStdout':'fake: OK\n'*37,'baseline':{'oomKill':0},'afterTransfer':{'oomKill':0}}
  files={'acceptance':{'text':json.dumps(acceptance)},'outerActualExit':{'text':json.dumps({'actualOuterReturnCode':0})},'records/exit.code':{'text':'0'},'records/terminal.txt':{'text':'fake exact terminal'},'records/SHA256SUMS':{'text':fs.nodes[raw+'/SHA256SUMS']['raw'].decode()}}
  for n,h in first.items():files['records/'+n]['sha256']=h
  release=json.loads(fs.nodes[root+'/release.stdout']['raw']);final=json.loads(fs.nodes[root+'/external-final.stdout']['raw'])
  for n in [root+'/executor-state.json',root+'/finite-phase-start.json']:del fs.nodes[n]
  rt.phase_outputs={'route-config':b'f7-fixture.invalid\n','poststage':{'passed':True,'oomKill':0,'files':{'transferReceipt':{'text':json.dumps(terminal)}},'allStageFiles0444':True,'stageStoredFiles':38,'allStageSha256':{n:v['sha256'] for n,v in expected.items()}},'postnative':{'files':files},'release':release,'external-final':final,'rawrecords':b'fake bounded archive source'}
  rt.phase_stderr={'stage':(json.dumps({'ACTUAL_STAGE_TERMINAL':terminal})+'\n').encode()}
  prefix=root+'/captured-rawrecords/';rt.archive_files={n[len(prefix):]:v['raw'] for n,v in fs.nodes.items() if n.startswith(prefix) and not v['directory']}
  for n in list(fs.nodes):
   if n.startswith(prefix) or n==prefix.rstrip('/'):del fs.nodes[n]
  rows=[{'path':n,'bytes':len(b),'sha256':digest(b),'mode':292} for n,b in rt.archive_files.items()]
  rt.phase_stderr['rawrecords']=(json.dumps({'RAWRECORDS_CAPTURE_TERMINAL':0,'originalFIRSTBefore':first,'originalFIRSTAfter':first,'files':rows})+'\n').encode()
 return raw,first


# v6 instrumentation is private fixture evidence, distinct from selected output.
# Named injections record the exact action and preconditions where reached.
class V6Path(V5Path):
 def read_bytes(self):
  failure=self.fs.case.get('failure');tail=self.key.rsplit('/',1)[-1]
  if '/proc/2100/' in self.key and failure=='identity-'+tail+'-read':
   self.fs.runtime.event(failure,'owned-'+tail+'-read','OSError',5)
  if '/proc/2100/' in self.key and failure in ('identity-UID','identity-argv') and tail in ('status','cmdline'):
   if (failure,tail) in (('identity-UID','status'),('identity-argv','cmdline')):self.fs.runtime.event(failure,'owned-'+tail)
  if self.key=='/proc/2100/stat' and failure=='identity-PGID':self.fs.runtime.event(failure,'owned-stat')
  return super().read_bytes()
 def stat(self):
  failure=self.fs.case.get('failure');rt=self.fs.runtime
  if self.key=='/f/executor/result.json' and failure=='mode-EIO':rt.event(failure,'postcapture-mode','OSError',5)
  if self.key=='/proc/2100' and failure=='identity-root-read':rt.event(failure,'owned-root-read','PermissionError',13)
  if self.key=='/proc/2100/exe' and failure=='identity-exe':rt.event(failure,'owned-exe')
  if self.key=='/proc/2100' and failure=='pidfd-race' and rt.pidfd_seen:rt.event(failure,'owned-root-after-pidfd')
  value=super().stat()
  if self.key=='/f' and failure=='parent-identity' and rt.created:
   rt.event(failure,'parent-identity');value.st_ino+=1
  if self.key=='/f' and failure=='parent-link':
   rt.event(failure,'parent-link');value.st_mode=stat.S_IFLNK|0o777
  if self.key=='/f' and failure=='parent-UID':
   rt.event(failure,'parent-UID');value.st_uid=1001
  return value

 def lstat(self):
  # Inspection fails after the partial write. Descriptor verification uses
  # stat/fstat and must not consume this distinct lstat-only injection.
  failure=self.fs.case.get('failure')
  if self.key.endswith('.postseal.check-output-fallback.json') and failure in ('fallback-EIO','fallback-EACCES','fallback-ENOTDIR'):
   number={'fallback-EIO':5,'fallback-EACCES':13,'fallback-ENOTDIR':20}[failure]
   kind={5:'OSError',13:'PermissionError',20:'NotADirectoryError'}[number]
   self.fs.runtime.event(failure,'fallback-lstat',kind,number)
   raise OSError(number,'fake partial exists but lstat failed',self.key)
  return super().lstat()

class V6FS(V5FS):
 def Path(self,name):return name if isinstance(name,V6Path) else V6Path(self,str(name))
 def fsync(self,fd):
  key=self.fds[fd];rt=self.runtime;failure=self.case.get('failure')
  if key.endswith('.journal') and failure=='journal-fsync':rt.event(failure,'journal-file-fsync','OSError',5)
  if key.endswith('.late-signals.jsonl') and failure=='late-fsync':
   if self.case.get('signal_at')=='late-fsync' and not rt.injected:
    rt.injected=True;rt.event('signal','signal-late-fsync');rt.handlers[15](15,None)
   self.log.append(['fsync',key]);rt.event(failure,'late-file-fsync','OSError',5);raise OSError(5,'fake initial late-signal fsync EIO',key)
  if self.nodes[key]['directory']:
   rt.directory_barriers+=1
   barrier=rt.directory_barriers
   targets={'journal-directory':1,'stdout-directory':2,'stderr-directory':3,'terminal-directory':4,'late-directory':1}
   if failure in targets and barrier==targets[failure]:
    self.log.append(['fsync',key]);rt.event(failure,failure+'-fsync','OSError',5);raise OSError(5,'fake directory publication EIO',key)
  return super().fsync(fd)

class V6Runtime(V5Runtime):
 def __init__(self,case):
  super().__init__(case);self.events=[];self.attempts=[];self.created=[];self.directory_barriers=0;self.closed_fds=set();self.diagnostics=[]
  self.fs=V6FS(case);self.fs.runtime=self;self.pathlib=types.SimpleNamespace(Path=self.fs.Path,PurePosixPath=pathlib.PurePosixPath)
  self.capture.ROOT=self.fs.Path('/f/executor');self.os.O_RDONLY=0;self.os.O_DIRECTORY=65536
  self.os.open=self.open_fd;self.os.stat=self.stat_at;self.os.fstat=self.fstat;self.os.fsync=self.fs.fsync
 def event(self,name,phase,error_type=None,errno=None):
  item={'name':name,'phase':phase,'type':error_type,'errno':errno,'obtainedBytes':{k:len(v) for k,v in self.obtained.items()},'PopenAttempts':len(self.attempts),'PopenCount':sum(c[0]=='Popen' for c in self.calls),'waitObserved':self.reaped,'barrierReleased':any(c[0]=='barrier-write' for c in self.calls),'journalPresent':'/f/executor.postseal.check-retention.journal' in self.fs.nodes}
  self.events.append(item);return item
 def path_at(self,name,dir_fd=None):
  return str(pathlib.PurePosixPath(str(name)) if dir_fd is None else pathlib.PurePosixPath(self.fs.fds[dir_fd])/str(name))
 def stat_at(self,name,dir_fd=None,follow_symlinks=True):return self.fs.Path(self.path_at(name,dir_fd)).stat()
 def fstat(self,fd):return self.fs.Path(self.fs.fds[fd]).stat()
 def open_fd(self,name,flags,mode=0o777,dir_fd=None):
  key=self.path_at(name,dir_fd);failure=self.case.get('failure');self.fs.log.append(['fd-open-at',key,flags,dir_fd])
  if flags & self.os.O_DIRECTORY:
   if failure=='parent-open' and key=='/f':self.event(failure,'parent-open','PermissionError',13);raise PermissionError(13,'fake parent open EACCES',key)
   assert self.fs.Path(key).is_dir(),'fake directory open requires directory'
  else:
   assert flags & self.os.O_EXCL and flags & self.os.O_NOFOLLOW
   if key.endswith('.late-signals.jsonl') and failure in ('late-open','late-EACCES'):
    number=28 if failure=='late-open' else 13;kind='OSError' if number==28 else 'PermissionError'
    self.event(failure,'late-open',kind,number);raise OSError(number,'fake late-signal initial open failure',key)
   if key.endswith('.postseal.check-output-fallback.json') and failure=='fallback-ENOENT':self.event(failure,'fallback-open','OSError',28);raise OSError(28,'fake create failed before publication',key)
   if key in self.fs.nodes:raise FileExistsError(17,'fake create-only existing',key)
   self.fs.put(key);self.created.append(key)
  self.fs.nextfd+=1;self.fs.fds[self.fs.nextfd]=key;return self.fs.nextfd
 def write(self,fd,body):
  if fd==91:return FakeRuntime.write(self,fd,body)
  if fd==2:self.diagnostics.append(bytes(body).decode('utf-8'));return len(body)
  if fd in self.closed_fds:raise OSError(9,'fake owned fd already closed')
  key=self.fs.fds[fd];failure=self.case.get('failure');self.fs.log.append(['fd-write',key,len(body)])
  if key.endswith('.finalization-STOP.json') and self.case.get('secondary')=='preserve-STOP-write':
   self.fs.nodes[key]['raw']+=body[:17];self.fs.nodes[key]['blocks']=8;self.event('secondary','preserve-STOP-write','OSError',5);raise OSError(5,'fake secondary preservation EIO',key)
  if key.endswith('.late-signals.jsonl') and failure=='signal-write':
   self.fs.nodes[key]['raw']+=body[:1];self.fs.nodes[key]['blocks']=8;self.event(failure,'late-signal-write','OSError',28);raise OSError(28,'fake partial late signal write',key)
  if key.endswith('.journal') and failure=='journal-short':self.event(failure,'journal-write');body=body[:17]
  if key.endswith('.postseal.check-stdout.raw') and failure=='stdout-memory':self.event(failure,'stdout-write','MemoryError');raise MemoryError('fake raw stdout allocation failure')
  if key.endswith('.postseal.check-stderr.raw') and failure=='stderr-FSIZE':
   self.fs.nodes[key]['raw']+=body[:17];self.fs.nodes[key]['blocks']=8;self.event(failure,'stderr-write','OSError',27);raise OSError(27,'fake FSIZE partial write',key)
  if key.endswith('.postseal.check-output-fallback.json') and failure in ('fallback-EIO','fallback-EACCES','fallback-ENOTDIR'):
   self.fs.nodes[key]['raw']+=body[:17];self.fs.nodes[key]['blocks']=8;self.event(failure,'fallback-write','OSError',28);raise OSError(28,'fake partial fallback publication',key)
  self.fs.nodes[key]['raw']+=body;self.fs.nodes[key]['blocks']=((len(self.fs.nodes[key]['raw'])+4095)//4096)*8;return len(body)
 def close(self,fd):
  FakeRuntime.close(self,fd)
  if fd in self.closed_fds:raise OSError(9,'fake close already attempted; no retry')
  self.closed_fds.add(fd)
  if fd in self.fs.fds:
   key=self.fs.fds[fd];self.fs.log.append(['fd-close',key,fd])
   if self.case.get('failure')=='parent-close' and key=='/f' and b'RECEIPT_OUTPUT_COMMITTED' in self.fs.nodes.get('/f/executor.postseal.check-retention.journal',{}).get('raw',b''):
    self.event('parent-close','parent-close','OSError',5);raise OSError(5,'fake final parent close EIO',key)
 def inject(self,phase):
  if not self.injected and self.case.get('inject')==phase:self.event('signal','signal-'+phase)
  return super().inject(phase)
 def print_record(self,*args,**kw):
  if self.case.get('failure') in ('print','print-flush'):self.event(self.case['failure'],'receipt-print','OSError',5)
  return super().print_record(*args,**kw)
 def readlink(self,name):
  if self.case.get('failure')=='identity-exe-read':self.event('identity-exe-read','owned-exe-read','OSError',5)
  return super().readlink(name)
 def pidfd_open(self,pid,flags):
  if self.case.get('failure')=='pidfd-open':self.event('pidfd-open','owned-pidfd-open','OSError',24)
  return super().pidfd_open(pid,flags)
 def popen(self,argv,**kw):
  self.attempts.append(list(argv))
  self.fs.log.append(['checker-attempt',list(argv)])
  if self.case.get('failure')=='Popen':self.event('Popen','checker-Popen','OSError',12)
  child=super().popen(argv,**kw)
  if self.case.get('failure')=='stream-close':
   def close_fault():self.event('stream-close','checker-stream-close','OSError',5);raise OSError(5,'fake stream close EIO')
   child.stdout.close=close_fault
  return child
 def read(self,fd,count):
  if self.case.get('failure')=='read-memory':self.event('read-memory','checker-read','MemoryError')
  if self.case.get('checker')=='partial' and fd==102 and self.remaining[fd]==[b'']:self.event('partial','checker-stderr-EOF-UNKNOWN','BlockingIOError',11)
  value=super().read(fd,count)
  if self.case.get('failure')=='stream-overflow' and any(len(v)>1048576 for v in self.obtained.values()):self.event('stream-overflow','checker-stream-bound')
  return value
 def wait4(self,pid,flags):
  if self.case['route']=='execute':self.calls.append(['wait4',pid,flags]);self.reaped=True;return pid,0,self.usage()
  if self.case.get('checker')=='wait-error':self.event('wait-error','checker-wait','OSError',5)
  if self.case.get('checker')=='wait-unknown':self.event('wait-unknown','checker-wait-UNKNOWN')
  return super().wait4(pid,flags)
 def selector(self):
  selector=super().selector();register=selector.register;select=selector.select
  def logged_register(*a):
   if self.case.get('failure')=='selector-register':self.event('selector-register','checker-selector-register','OSError',12)
   return register(*a)
  def logged_select(t):
   if self.case.get('failure')=='selector-select':self.event('selector-select','checker-selector-select','OSError',5)
   value=select(t)
   if t and self.case.get('checker') in ('timeout','wait-unknown','partial') and self.clock.current==31:self.event('timeout','checker-deadline')
   return value
  selector.register=logged_register;selector.select=logged_select;return selector
 def load_selected(self,route):
  # Wrap fixture JSON/base64 fault sites before the complete selected source
  # runs. Events are produced only by calls that actually reach that fault site.
  if route in self.modules:return self.modules[route]
  if route=='publication' and self.case.get('decoy')=='unrelated-before-capture':
   # Discriminator: a real unrelated exception, with no named injection event.
   raise MemoryError('unrelated early allocation failure; not named mode-EIO')
  namespace=super().load_selected(route)
  if route in ('afterseal','finalizer','preserve_incomplete'):
   module=namespace.get('json');original=module.dumps
   def logged_dumps(value,*a,**kw):
    failure=self.case.get('failure')
    if isinstance(value,dict):
     if failure=='fallback-JSON-memory' and 'stdoutBase64' in value:self.event(failure,'fallback-JSON','MemoryError')
     if failure=='terminal-JSON-memory' and 'argv' in value:self.event(failure,'terminal-JSON','MemoryError')
     if failure=='output-JSON-memory' and 'BS-F2' in value:self.event(failure,'receipt-JSON','MemoryError')
    return original(value,*a,**kw)
   module.dumps=logged_dumps
   b64module=namespace.get('base64')
   if b64module is not None:
    original_b64=b64module.b64encode
    def logged_b64(value):
     if self.case.get('failure')=='fallback-base64-memory' and bytes(value)==self.raw['stdout']:self.event('fallback-base64-memory','fallback-base64','MemoryError')
     return original_b64(value)
    b64module.b64encode=logged_b64
  return namespace

# seed_runtime/seed_v5 and selected fake imports resolve these at future runtime.
V5Path=V6Path
V5Runtime=V6Runtime

FAULT_SITES={
 'mode-EIO':[('postcapture-mode','OSError',5)],
 'fallback-JSON-memory':[('fallback-JSON','MemoryError',None)],
 'fallback-base64-memory':[('fallback-base64','MemoryError',None)],
 'terminal-JSON-memory':[('terminal-JSON','MemoryError',None)],
 'output-JSON-memory':[('receipt-JSON','MemoryError',None)],
 'print':[('receipt-print','OSError',5)],'print-flush':[('receipt-print','OSError',5)],
 'stdout-memory':[('stdout-write','MemoryError',None)],'stderr-FSIZE':[('stderr-write','OSError',27)],
 'fallback-EIO':[('fallback-write','OSError',28),('fallback-lstat','OSError',5)],
 'fallback-EACCES':[('fallback-write','OSError',28),('fallback-lstat','PermissionError',13)],
 'fallback-ENOTDIR':[('fallback-write','OSError',28),('fallback-lstat','NotADirectoryError',20)],
 'fallback-ENOENT':[('fallback-open','OSError',28)],
 'journal-fsync':[('journal-file-fsync','OSError',5)],'journal-short':[('journal-write',None,None)],
 'identity-UID':[('owned-status',None,None)],'identity-PGID':[('owned-stat',None,None)],
 'identity-argv':[('owned-cmdline',None,None)],'identity-exe':[('owned-exe',None,None)],
 'identity-root-read':[('owned-root-read','PermissionError',13)],
 'identity-stat-read':[('owned-stat-read','OSError',5)],'identity-status-read':[('owned-status-read','OSError',5)],
 'identity-cmdline-read':[('owned-cmdline-read','OSError',5)],'identity-exe-read':[('owned-exe-read','OSError',5)],
 'pidfd-open':[('owned-pidfd-open','OSError',24)],'pidfd-race':[('owned-root-after-pidfd',None,None)],
 'Popen':[('checker-Popen','OSError',12)],'selector-register':[('checker-selector-register','OSError',12)],
 'selector-select':[('checker-selector-select','OSError',5)],'read-memory':[('checker-read','MemoryError',None)],
 'stream-overflow':[('checker-stream-bound',None,None)],'stream-close':[('checker-stream-close','OSError',5)],
 'disk':[('resource-disk',None,None)],'RSS':[('resource-RSS',None,None)],'component':[('resource-component',None,None)],
 'signal-write':[('late-signal-write','OSError',28)],
 'parent-open':[('parent-open','PermissionError',13)],'parent-link':[('parent-link',None,None)],'parent-UID':[('parent-UID',None,None)],
 'parent-identity':[('parent-identity',None,None)],'parent-close':[('parent-close','OSError',5)],
 'journal-directory':[('journal-directory-fsync','OSError',5)],'stdout-directory':[('stdout-directory-fsync','OSError',5)],
 'stderr-directory':[('stderr-directory-fsync','OSError',5)],'terminal-directory':[('terminal-directory-fsync','OSError',5)],
 'late-open':[('late-open','OSError',28)],'late-EACCES':[('late-open','PermissionError',13)],
 'late-fsync':[('late-file-fsync','OSError',5)],'late-directory':[('late-directory-fsync','OSError',5)],
}
PRECAPTURE={'journal-fsync','journal-short','journal-directory','parent-open','parent-link','parent-UID','parent-identity'}
FULL_CAPTURE_TAIL={'mode-EIO','fallback-JSON-memory','fallback-base64-memory','terminal-JSON-memory','output-JSON-memory','print','print-flush','stdout-memory','stderr-FSIZE','fallback-EIO','fallback-EACCES','fallback-ENOTDIR','fallback-ENOENT','stdout-directory','stderr-directory','terminal-directory','parent-close'}

NEW_V6_CASES=[
 {'id':'v6-parent-open-EACCES-checker-UNRUN','route':'afterseal','checker':'normal','failure':'parent-open','expected':1},
 {'id':'v6-parent-link-rejected-checker-UNRUN','route':'afterseal','checker':'normal','failure':'parent-link','expected':1},
 {'id':'v6-parent-UID-rejected-checker-UNRUN','route':'afterseal','checker':'normal','failure':'parent-UID','expected':1},
 {'id':'v6-parent-identity-after-create-checker-UNRUN','route':'afterseal','checker':'normal','failure':'parent-identity','expected':1},
 {'id':'v6-journal-directory-EIO-before-checker','route':'afterseal','checker':'normal','failure':'journal-directory','expected':1},
 {'id':'v6-stdout-directory-EIO-raw-kept-red','route':'afterseal','checker':'normal','failure':'stdout-directory','expected':1},
 {'id':'v6-stderr-directory-EIO-raw-kept-red','route':'afterseal','checker':'normal','failure':'stderr-directory','expected':1},
 {'id':'v6-terminal-directory-EIO-wait-kept-red','route':'afterseal','checker':'normal','failure':'terminal-directory','expected':1},
 {'id':'v6-parent-close-EIO-printed-zero-provisional','route':'afterseal','checker':'normal','failure':'parent-close','expected':1},
 {'id':'v6-execute-late-open-ENOSPC-preserve-once','route':'execute','failure':'late-open','expected':1},
 {'id':'v6-execute-late-open-EACCES-preserve-once','route':'execute','failure':'late-EACCES','expected':1},
 {'id':'v6-execute-late-initial-fsync-EIO-partial-preserved','route':'execute','failure':'late-fsync','expected':1},
 {'id':'v6-execute-late-directory-EIO-partial-preserved','route':'execute','failure':'late-directory','expected':1},
 {'id':'v6-execute-late-fsync-secondary-preservation-EIO-outer-red','route':'execute','failure':'late-fsync','secondary':'preserve-STOP-write','expected':1},
 {'id':'v6-execute-late-fsync-live-TERM-remains-red','route':'execute','failure':'late-fsync','signal_at':'late-fsync','expected':143},
]
ORACLE_DISCRIMINATORS=[
 {'id':'v6-oracle-reject-unrelated-early-MemoryError','discriminator':'unrelated','reject':'NAMED_TRIGGER'},
 {'id':'v6-oracle-reject-wrong-phase','discriminator':'wrong-phase','reject':'NAMED_PHASE'},
 {'id':'v6-oracle-reject-missing-trigger','discriminator':'missing-trigger','reject':'NAMED_TRIGGER'},
 {'id':'v6-oracle-reject-missing-stdout','discriminator':'missing-raw','reject':'RAW_REQUIRED'},
 {'id':'v6-oracle-reject-missing-wait','discriminator':'missing-wait','reject':'WAIT_REQUIRED'},
 {'id':'v6-oracle-reject-unrelated-outer-error','discriminator':'unrelated-error','reject':'UNEXPECTED_EXCEPTION'},
 {'id':'v6-oracle-reject-missing-output','discriminator':'missing-output','reject':'OUTPUT_REQUIRED'},
]
V6_CASES=V5_CASES+NEW_V6_CASES+ORACLE_DISCRIMINATORS

class OracleReject(AssertionError):
 def __init__(self,predicate,detail):self.predicate=predicate;super().__init__(predicate+': '+detail)
def require(condition,predicate,detail):
 if not condition:raise OracleReject(predicate,detail)

# v7 independent fixture observations: these are produced at the actual fake
# call boundary, before any selected terminal artifact is read by the oracle.
# They qualify this private model only; real kernel/adapter proof stays UNRUN.
WAIT_USAGE_FIXTURE={'ru_utime':0.125,'ru_stime':0.25,'ru_maxrss':2048,'ru_inblock':3,'ru_oublock':5,'ru_nvcsw':7,'ru_nivcsw':11}
class V7Runtime(V6Runtime):
 def __init__(self,case):
  super().__init__(case);self.wait_observations=[];self.identity_observations=[];self.output_observations=[]
  self.capture.identity=self.observed_identity
 def observed_identity(self,pid):
  value={'pid':pid,'ppid':2000,'uid':1000,'pgid':pid,'sid':pid,'birth':17}
  self.identity_observations.append(dict(value));return value
 def usage(self):
  return types.SimpleNamespace(**WAIT_USAGE_FIXTURE) if self.case['route']=='execute' else super().usage()
 def popen(self,argv,**kw):
  child=super().popen(argv,**kw)
  if self.case['route']=='execute':
   root='/f/executor';out=bytes(self.fs.nodes[root+'.postseal.final.json']['raw']);err=bytes(self.fs.nodes[root+'.postseal.final.stderr']['raw'])
   self.output_observations.append({'pid':child.pid,'argv':list(argv),'passFds':list(kw['pass_fds']),'startNewSession':kw['start_new_session'],'stdout':out,'stderr':err,'manifestBytes':bytes(self.fs.nodes[root+'/SHA256SUMS']['raw'])})
  return child
 def wait4(self,pid,flags):
  if self.case['route']!='execute':
   observed_pid,status,usage=super().wait4(pid,flags)
   if observed_pid:
    self.wait_observations.append({'pid':observed_pid,'flags':flags,'rawWaitStatus':status,'actualReturnCode':self.os.waitstatus_to_exitcode(status),'WIFEXITED':self.os.WIFEXITED(status),'WEXITSTATUS':self.os.WEXITSTATUS(status) if self.os.WIFEXITED(status) else None,'WIFSIGNALED':self.os.WIFSIGNALED(status),'WTERMSIG':self.os.WTERMSIG(status) if self.os.WIFSIGNALED(status) else None,'wait4usage':{k:getattr(usage,k) for k in WAIT_USAGE_FIXTURE}})
   return observed_pid,status,usage
  require(pid==self.pid and flags==0,'FIXTURE_WAIT_CALL','whole-execute owned blocking wait')
  self.calls.append(['wait4',pid,flags]);usage=self.usage();self.reaped=True
  observation={'pid':pid,'flags':flags,'rawWaitStatus':0,'actualReturnCode':0,'WIFEXITED':True,'WEXITSTATUS':0,'WIFSIGNALED':False,'WTERMSIG':None,'wait4usage':{k:getattr(usage,k) for k in WAIT_USAGE_FIXTURE}}
  self.wait_observations.append(observation);return pid,0,usage

V6Runtime=V7Runtime

def execute_json_required(fs,path,predicate):
 require(path in fs.nodes and not fs.nodes[path]['directory'],predicate,'mandatory regular '+path)
 try:value=json.loads(fs.nodes[path]['raw'])
 except (ValueError,UnicodeError,TypeError) as error:raise OracleReject(predicate,'invalid JSON '+path+' '+type(error).__name__)
 require(type(value) is dict,predicate,'typed object '+path);return value

def whole_execute_terminal(case,state,secondary=False):
 rt=state['rt'];fs=rt.fs;root='/f/executor'
 paths={k:root+v for k,v in {'wait':'.postseal.actual-wait.json','identity':'.postseal.child-before-work.json','stdout':'.postseal.final.json','stderr':'.postseal.final.stderr'}.items()}
 if secondary:
  # This named secondary STOP write dies before any final sample starts.
  # UNKNOWN means this precise absence, never a fabricated wait or empty object.
  require(state['error'] is not None and state['error']['type']=='OSError' and state['error']['errno']==5 and any(e['name']=='secondary' and e['phase']=='preserve-STOP-write' for e in rt.events),'EXEC_UNKNOWN_CAUSE','specific pre-sample secondary EIO')
  require(not rt.wait_observations and not rt.identity_observations and not rt.output_observations and not rt.output and all(p not in fs.nodes for p in paths.values()),'EXEC_UNKNOWN_CAUSE','no sample or synthetic terminal evidence; exact pre-sample UNKNOWN')
  return {'status':'UNKNOWN','cause':'named preserve-STOP-write EIO before final sample','rawPartialPath':root+'.finalization-STOP.json','rawPartialBytes':17,'actualOuterError':dict(state['error'])}
 require(len(rt.wait_observations)==len(rt.identity_observations)==len(rt.output_observations)==1,'EXEC_OBSERVATIONS','exactly one independently observed sample')
 observed=rt.wait_observations[0];identity=rt.identity_observations[0];outputs=rt.output_observations[0]
 wait=execute_json_required(fs,paths['wait'],'EXEC_WAIT_REQUIRED')
 preservation=case.get('failure') in ('late-open','late-EACCES','late-fsync','late-directory')
 keys={'utc','pid','rawWaitStatus','actualReturnCode','WIFEXITED','WEXITSTATUS','WIFSIGNALED','WTERMSIG','wait4usage','rawWait4NotOwnRSS','ownSignalsSent'}|({'preservationOnly'} if preservation else set())
 require(set(wait)==keys and type(wait['utc']) is str and wait['utc']=='FAKE-UTC-NOT-OBSERVED','EXEC_WAIT_SHAPE','complete selected terminal schema')
 for key in ('pid','rawWaitStatus','actualReturnCode','WEXITSTATUS'):
  require(type(wait[key]) is int and wait[key]==observed[key],'EXEC_WAIT_STATUS',key)
 require(type(wait['WIFEXITED']) is bool and wait['WIFEXITED'] is observed['WIFEXITED'] and type(wait['WIFSIGNALED']) is bool and wait['WIFSIGNALED'] is observed['WIFSIGNALED'] and wait['WTERMSIG'] is observed['WTERMSIG'],'EXEC_WAIT_STATUS','typed exit/signal status')
 require(wait['rawWait4NotOwnRSS'] is True and type(wait['ownSignalsSent']) is list and wait['ownSignalsSent']==[] and (not preservation or wait['preservationOnly'] is True),'EXEC_WAIT_SHAPE','selected wait provenance and signal fields')
 usage=wait['wait4usage'];require(type(usage) is dict and set(usage)==set(WAIT_USAGE_FIXTURE),'EXEC_USAGE','complete usage fields')
 for key,expected in observed['wait4usage'].items():require(type(usage[key]) is type(expected) and usage[key]==expected,'EXEC_USAGE',key)
 before=execute_json_required(fs,paths['identity'],'EXEC_IDENTITY_REQUIRED')
 require(set(before)=={'actual','barrier','noNamespaceMapping'}|({'preservationOnly'} if preservation else set()) and before['barrier']=='owned pipe, unreleased' and before['noNamespaceMapping'] is True and (not preservation or before['preservationOnly'] is True),'EXEC_IDENTITY','typed barrier provenance')
 actual=before['actual'];require(type(actual) is dict and set(actual)==set(identity) and all(type(actual[k]) is int and actual[k]==v for k,v in identity.items()),'EXEC_IDENTITY','exact independent pid/ppid/UID/PGID/SID/birth')
 require(identity['pid']==observed['pid']==outputs['pid']==rt.pid and identity['ppid']==2000 and identity['uid']==1000 and identity['pgid']==identity['sid']==rt.pid and outputs['startNewSession'] is True and outputs['passFds']==[90],'EXEC_IDENTITY','observations agree with owned barrier and wait')
 for stream in ('stdout','stderr'):
  path=paths[stream];require(path in fs.nodes and not fs.nodes[path]['directory'],'EXEC_RAW_REQUIRED',stream)
  actual_raw=fs.nodes[path]['raw'];expected_raw=outputs[stream]
  require(type(actual_raw) is bytes and actual_raw==expected_raw and len(actual_raw)==len(expected_raw) and digest(actual_raw)==digest(expected_raw),'EXEC_RAW_AGREEMENT',stream+' exact obtained bytes/length/hash')
 receipt=execute_json_required(fs,paths['stdout'],'EXEC_OUTPUT_REQUIRED')
 expected_receipt=json.loads(outputs['stdout'])
 require(receipt==expected_receipt and receipt.get('passed') is True and type(receipt.get('fullManifestActualShaCExit')) is int and receipt['fullManifestActualShaCExit']==observed['actualReturnCode']==0 and type(receipt.get('fullManifestRows')) is int and receipt['fullManifestRows']==len(outputs['manifestBytes'].splitlines()) and type(receipt.get('fullManifestCheckStdoutEncoded')) is dict,'EXEC_OUTPUT_AGREEMENT','exact fixture output and complete manifest receipt')
 require(fs.nodes[root+'/SHA256SUMS']['raw']==outputs['manifestBytes'],'EXEC_OUTPUT_AGREEMENT','unchanged sealed manifest sampled by child')
 require(sum(c[0]=='wait4' and c[1:]==[rt.pid,0] for c in rt.calls)==1 and sum(c[0]=='barrier-write' and c[1:]==[91,'31'] for c in rt.calls)==1 and sum(c[0]=='Popen' for c in rt.calls)==len(rt.attempts)==1,'EXEC_OBSERVATIONS','exact wait/barrier/Popen counts')
 require(len(rt.output)==1 and type(rt.output[0]) is str,'EXEC_PRINT_REQUIRED','exact one selected whole-execute terminal emission')
 try:terminal=json.loads(rt.output[0])
 except (ValueError,UnicodeError,TypeError) as error:raise OracleReject('EXEC_PRINT_REQUIRED','invalid whole-execute terminal JSON '+type(error).__name__)
 require(type(terminal) is dict,'EXEC_PRINT_REQUIRED','typed selected terminal object')
 if preservation:
  require(set(terminal)=={'FINITE_TESTEND','actualExecutorExit','fullManifestSHA256','externalFinalReceipt','finalizerStop','noFurtherAction'} and terminal['FINITE_TESTEND']=='INCOMPLETE / consumed' and type(terminal['actualExecutorExit']) is int and terminal['actualExecutorExit']==1 and terminal['fullManifestSHA256']==digest(outputs['manifestBytes']) and terminal['externalFinalReceipt']==paths['stdout'] and terminal['finalizerStop']==root+'.finalization-STOP.json' and terminal['noFurtherAction'] is True,'EXEC_PRINT_AGREEMENT','precise preserved incomplete emission; actual outer stays red')
 else:
  require(type(terminal.get('FINITE_TESTEND')) is int and terminal['FINITE_TESTEND']==(1 if case.get('inject') in ('snapshot','result') else 0) and type(terminal.get('actualExternalAfterSealExit')) is int and terminal['actualExternalAfterSealExit']==observed['actualReturnCode'] and terminal.get('originalFIRSTHashes')==state['FIRST'] and terminal.get('externalReceipt')==paths['stdout'] and terminal.get('externalWait')==paths['wait'] and terminal.get('executorManifestSha256')==digest(outputs['manifestBytes']) and terminal.get('resultSha256')==digest(fs.nodes[root+'/result.json']['raw']) and type(terminal.get('fullEntries')) is int and terminal['fullEntries']==len(outputs['manifestBytes'].splitlines()) and terminal.get('terminalIsProvisionalUntilActualWaitAndPostEmissionElapsed') is True,'EXEC_PRINT_AGREEMENT','complete provisional terminal agrees with raw child wait/output/seal/result/FIRST')
 return {'status':'KNOWN_FAKE_ONLY','waitPath':paths['wait'],'identityPath':paths['identity'],'streams':{k:{'path':paths[k],'bytes':len(outputs[k]),'sha256':digest(outputs[k])} for k in ('stdout','stderr')},'observedWait':observed,'observedIdentity':identity,'actualKernelOrAdapterProof':False}

WHOLE_EXECUTE_DISCRIMINATORS=[
 {'id':'v7-execute-oracle-reject-missing-wait','wholeDiscriminator':'missing-wait','reject':'EXEC_WAIT_REQUIRED'},
 {'id':'v7-execute-oracle-reject-empty-wait','wholeDiscriminator':'empty-wait','reject':'EXEC_WAIT_SHAPE'},
 {'id':'v7-execute-oracle-reject-corrupt-wait','wholeDiscriminator':'corrupt-wait','reject':'EXEC_WAIT_REQUIRED'},
 {'id':'v7-execute-oracle-reject-raw-status','wholeDiscriminator':'raw-status','reject':'EXEC_WAIT_STATUS'},
 {'id':'v7-execute-oracle-reject-return-code','wholeDiscriminator':'return-code','reject':'EXEC_WAIT_STATUS'},
 {'id':'v7-execute-oracle-reject-bool-status','wholeDiscriminator':'bool-status','reject':'EXEC_WAIT_STATUS'},
 {'id':'v7-execute-oracle-reject-missing-usage','wholeDiscriminator':'missing-usage','reject':'EXEC_USAGE'},
 {'id':'v7-execute-oracle-reject-mismatched-usage','wholeDiscriminator':'usage','reject':'EXEC_USAGE'},
 {'id':'v7-execute-oracle-reject-missing-identity','wholeDiscriminator':'missing-identity','reject':'EXEC_IDENTITY_REQUIRED'},
 {'id':'v7-execute-oracle-reject-mismatched-identity','wholeDiscriminator':'identity','reject':'EXEC_IDENTITY'},
 {'id':'v7-execute-oracle-reject-missing-stdout','wholeDiscriminator':'missing-stdout','reject':'EXEC_RAW_REQUIRED'},
 {'id':'v7-execute-oracle-reject-missing-stderr','wholeDiscriminator':'missing-stderr','reject':'EXEC_RAW_REQUIRED'},
 {'id':'v7-execute-oracle-reject-mismatched-stdout','wholeDiscriminator':'stdout','reject':'EXEC_RAW_AGREEMENT'},
 {'id':'v7-execute-oracle-reject-mismatched-stderr','wholeDiscriminator':'stderr','reject':'EXEC_RAW_AGREEMENT'},
 {'id':'v7-execute-oracle-reject-missing-wait-observation','wholeDiscriminator':'wait-observation','reject':'EXEC_OBSERVATIONS'},
 {'id':'v7-execute-oracle-reject-unrelated-error','wholeDiscriminator':'unrelated-error','reject':'UNEXPECTED_EXCEPTION'},
 {'id':'v7-execute-oracle-reject-missing-named-trigger','wholeDiscriminator':'missing-trigger','reject':'NAMED_TRIGGER'},
 {'id':'v7-execute-oracle-reject-wrong-named-phase','wholeDiscriminator':'wrong-phase','reject':'NAMED_PHASE'},
 {'id':'v7-execute-oracle-reject-corrupt-identity','wholeDiscriminator':'corrupt-identity','reject':'EXEC_IDENTITY_REQUIRED'},
 {'id':'v7-execute-oracle-reject-bool-usage','wholeDiscriminator':'bool-usage','reject':'EXEC_USAGE'},
 {'id':'v7-execute-oracle-reject-wrong-wait-pid','wholeDiscriminator':'wait-pid','reject':'EXEC_WAIT_STATUS'},
 {'id':'v7-execute-oracle-reject-missing-terminal-print','wholeDiscriminator':'missing-terminal','reject':'EXEC_PRINT_REQUIRED'},
 {'id':'v7-execute-oracle-reject-corrupt-terminal-print','wholeDiscriminator':'corrupt-terminal','reject':'EXEC_PRINT_REQUIRED'},
 {'id':'v7-execute-oracle-reject-mismatched-terminal-print','wholeDiscriminator':'terminal','reject':'EXEC_PRINT_AGREEMENT'},
]
V7_CASES=V6_CASES+WHOLE_EXECUTE_DISCRIMINATORS

def v7_case(case,on_state=None):
 if 'wholeDiscriminator' not in case:return v6_case(case,on_state=on_state)
 target=dict(next(c for c in NEW_V6_CASES if c['failure']=='late-open'));state=exercise(target);rt=state['rt'];root='/f/executor'
 if on_state is not None:on_state(state)
 # Prove the untouched intended route first. A missing artifact/trigger or
 # unrelated fixture failure cannot substitute for the named transformation.
 strict_oracle(target,state)
 which=case['wholeDiscriminator'];wait=root+'.postseal.actual-wait.json';before=root+'.postseal.child-before-work.json'
 if which=='missing-wait':del rt.fs.nodes[wait]
 elif which=='empty-wait':rt.fs.nodes[wait]['raw']=b'{}'
 elif which=='corrupt-wait':rt.fs.nodes[wait]['raw']=b'{'
 elif which in ('raw-status','return-code','bool-status','missing-usage','usage','bool-usage','wait-pid'):
  value=json.loads(rt.fs.nodes[wait]['raw'])
  if which=='raw-status':value['rawWaitStatus']=1
  elif which=='return-code':value['actualReturnCode']=1
  elif which=='bool-status':value['actualReturnCode']=False
  elif which=='missing-usage':value['wait4usage']={}
  elif which=='usage':value['wait4usage']['ru_inblock']+=1
  elif which=='bool-usage':value['wait4usage']['ru_inblock']=True
  else:value['pid']+=1
  rt.fs.json(wait,value)
 elif which=='missing-identity':del rt.fs.nodes[before]
 elif which=='identity':
  value=json.loads(rt.fs.nodes[before]['raw']);value['actual']['birth']+=1;rt.fs.json(before,value)
 elif which=='corrupt-identity':rt.fs.nodes[before]['raw']=b'{'
 elif which in ('missing-stdout','missing-stderr'):
  del rt.fs.nodes[root+('.postseal.final.json' if which.endswith('stdout') else '.postseal.final.stderr')]
 elif which in ('stdout','stderr'):
  rt.fs.nodes[root+('.postseal.final.json' if which=='stdout' else '.postseal.final.stderr')]['raw']+=b'CORRUPTED'
 elif which=='wait-observation':rt.wait_observations=[]
 elif which=='unrelated-error':state['error']={'type':'MemoryError','reason':'unrelated terminal allocation error','errno':None}
 elif which=='missing-trigger':rt.events=[]
 elif which=='wrong-phase':
  for event in rt.events:
   if event['name']=='late-open':event['phase']='unrelated-phase'
 elif which=='missing-terminal':rt.output=[]
 elif which=='corrupt-terminal':rt.output=['{']
 elif which=='terminal':
  value=json.loads(rt.output[0]);value['actualExecutorExit']=0;rt.output=[json.dumps(value)]
 else:raise OracleReject('UNKNOWN_DISCRIMINATOR',which)
 try:strict_oracle(target,state)
 except OracleReject as error:
  require(error.predicate==case['reject'],'DISCRIMINATOR_EXACT_REJECTION',str(error))
  return {'case':case['id'],'matched':True,'fakeOnly':True,'rejectedBy':error.predicate,'intendedRoutePrechecked':target['id'],'observed':snapshot(state)}
 raise OracleReject('DISCRIMINATOR_FALSE_ACCEPT',case['id'])

def exercise(case):
 rt=V6Runtime(dict(case));raw,first=seed_v5(rt)
 originals={n:bytes(v['raw']) for n,v in rt.fs.nodes.items() if n.startswith(raw+'/') and not v['directory']}
 if case['route']=='execute':originals={'/f/executor/captured-rawrecords/'+n:body for n,body in rt.archive_files.items()}
 failure=case.get('failure')
 if failure in ('disk','RSS','component'):rt.event(failure,'resource-'+failure)
 error=None;code=None
 try:
  module=rt.load_selected(case['route']);code=module['main']() if case['route']!='execute' else None
 except SystemExit as end:code=end.code
 except BaseException as end:error={'type':type(end).__name__,'reason':str(end),'errno':getattr(end,'errno',None)};code=1
 return {'rt':rt,'raw':raw,'FIRST':first,'originals':originals,'code':code,'error':error}

def same_json_types_and_values(left,right):
 if type(left) is not type(right):return False
 if type(left) is dict:return set(left)==set(right) and all(same_json_types_and_values(left[k],right[k]) for k in left)
 if type(left) is list:return len(left)==len(right) and all(same_json_types_and_values(a,b) for a,b in zip(left,right))
 return left==right

def afterseal_terminal(rt,terminal):
 keys={'argv','timeoutSeconds','startedMonotonic','finishedMonotonic','elapsedSeconds','timedOut','captureComplete','eof','actualReturnCode','actualWait','ownSignalsSent','errors','childIdentity','barrierReleased','drainLimitPerStreamBytes','noRetry'}
 optional={'barrierCommand','childPid','barrierIdentityEvidenceBeforePidfd','barrierIdentityEvidenceAfterPidfd','identityAfterPidfd'}
 require(type(terminal) is dict and keys<=set(terminal)<=keys|optional,'CHECK_TERMINAL_SHAPE','complete checker terminal schema; only declared partial barrier fields')
 require(type(terminal['argv']) is list and terminal['argv']==['sha256sum','-c','SHA256SUMS'] and type(terminal['timeoutSeconds']) is int and terminal['timeoutSeconds']==30 and type(terminal['drainLimitPerStreamBytes']) is int and terminal['drainLimitPerStreamBytes']==1048576 and terminal['noRetry'] is True,'CHECK_TERMINAL_SHAPE','typed checker argv, limits and no retry')
 require(all(type(terminal[k]) in (int,float) and math.isfinite(terminal[k]) for k in ('startedMonotonic','finishedMonotonic','elapsedSeconds')) and all(type(terminal[k]) is bool for k in ('timedOut','captureComplete','barrierReleased')),'CHECK_TERMINAL_SHAPE','typed checker time and flags')
 if 'barrierCommand' in terminal:require(type(terminal['barrierCommand']) is list and all(type(v) is str for v in terminal['barrierCommand']),'CHECK_TERMINAL_SHAPE','typed barrier command')
 if 'childPid' in terminal:require(type(terminal['childPid']) is int and terminal['childPid']==rt.pid,'CHECK_TERMINAL_SHAPE','typed owned child pid')
 for key in ('barrierIdentityEvidenceBeforePidfd','barrierIdentityEvidenceAfterPidfd'):
  if key in terminal:require(type(terminal[key]) is list and all(type(v) is dict for v in terminal[key]),'CHECK_TERMINAL_SHAPE','typed partial barrier evidence')
 if 'identityAfterPidfd' in terminal:require(type(terminal['identityAfterPidfd']) is dict,'CHECK_TERMINAL_SHAPE','typed identity after pidfd')
 require(type(terminal['eof']) is dict and set(terminal['eof'])=={'stdout','stderr'} and all(type(v) is bool for v in terminal['eof'].values()),'CHECK_TERMINAL_SHAPE','typed stream EOF observations')
 require(all(type(terminal[k]) is list and all(type(v) is dict for v in terminal[k]) for k in ('ownSignalsSent','errors')) and (terminal['childIdentity'] is None or type(terminal['childIdentity']) is dict),'CHECK_TERMINAL_SHAPE','typed checker signal, error and identity containers')
 if terminal['actualWait'] is None:
  require(not rt.wait_observations,'WAIT_REQUIRED','independently observed wait cannot be erased as UNKNOWN')
  require(terminal['actualReturnCode'] is None,'CHECK_WAIT_STATUS','unknown wait has no return code')
  return
 wait=terminal['actualWait'];keys={'pid','rawWaitStatus','WIFEXITED','WEXITSTATUS','WIFSIGNALED','WTERMSIG','actualReturnCode','wait4usage','rawWait4NotOwnRSS'}
 require(type(wait) is dict and set(wait)==keys and wait['rawWait4NotOwnRSS'] is True,'CHECK_WAIT_SHAPE','complete observed checker wait schema')
 require(len(rt.wait_observations)==1,'CHECK_WAIT_OBSERVATIONS','one independent actual fake wait')
 observed=rt.wait_observations[0]
 require(type(terminal['actualReturnCode']) is int and terminal['actualReturnCode']==observed['actualReturnCode'],'CHECK_WAIT_STATUS','typed top-level return code')
 for key in ('pid','rawWaitStatus','actualReturnCode','WEXITSTATUS','WTERMSIG'):
  require(type(wait[key]) is type(observed[key]) and wait[key]==observed[key],'CHECK_WAIT_STATUS',key)
 require(type(wait['WIFEXITED']) is bool and wait['WIFEXITED'] is observed['WIFEXITED'] and type(wait['WIFSIGNALED']) is bool and wait['WIFSIGNALED'] is observed['WIFSIGNALED'],'CHECK_WAIT_STATUS','typed exit/signal flags')
 usage=wait['wait4usage']
 require(type(usage) is dict and set(usage)==set(WAIT_USAGE_FIXTURE),'CHECK_USAGE','complete usage schema')
 for key,expected in observed['wait4usage'].items():
  require(type(usage[key]) is type(expected) and usage[key]==expected,'CHECK_USAGE',key)

def strict_oracle(case,state):
 rt=state['rt'];fs=rt.fs;root='/f/executor';failure=case.get('failure');code=state['code'];error=state['error']
 # Trigger identity is checked before expected nonzero. Missing/wrong injection
 # cannot be rescued by any unrelated exception, receipt, or healthy case.
 for phase,kind,number in FAULT_SITES.get(failure,[]):
  events=[e for e in rt.events if e['name']==failure]
  require(bool(events),'NAMED_TRIGGER',str(failure))
  matches=[e for e in events if (e['phase'],e['type'],e['errno'])==(phase,kind,number)]
  require(bool(matches),'NAMED_PHASE',str((failure,phase,kind,number)))
  if failure in FULL_CAPTURE_TAIL:
   require(all(e['PopenCount']==1 and e['waitObserved'] and e['barrierReleased'] for e in matches),'CAPTURE_PRECONDITION',failure)
   require(all(e['obtainedBytes']=={k:len(v) for k,v in rt.raw.items()} for e in matches),'CAPTURE_PRECONDITION',failure+' full streams')
 if case.get('inject'):
  require(rt.injected and any(e['name']=='signal' and e['phase']=='signal-'+case['inject'] for e in rt.events),'NAMED_TRIGGER','exact signal phase')
 checker_mode=case.get('checker')
 if checker_mode in ('wait-error','wait-unknown','partial'):
  require(any(e['name']==checker_mode for e in rt.events),'NAMED_TRIGGER',checker_mode)
 require(code==case['expected'],'EXACT_STATUS',repr((code,case['expected'],error)))
 require(all(n in fs.nodes and fs.nodes[n]['raw']==body for n,body in state['originals'].items()),'FIRST_PRESERVED','all original bytes')
 popen=sum(c[0]=='Popen' for c in rt.calls)
 setup=failure in PRECAPTURE
 setup_assertions={'journal-short':'retention journal short write; existing partial record preserved','parent-link':'publication parent type UNKNOWN','parent-UID':'publication parent expected UID UNKNOWN','parent-identity':'publication bound parent identity UNKNOWN'}
 allowed_outer=setup and error is not None and ((error['type'],error['errno']) in {(kind,number) for phase,kind,number in FAULT_SITES.get(failure,[]) if kind is not None} or failure in setup_assertions and error['type']=='AssertionError' and error['reason']==setup_assertions[failure])
 secondary=case.get('secondary')=='preserve-STOP-write'
 allowed_secondary=secondary and error is not None and error['type']=='OSError' and error['errno']==5 and any(e['name']=='secondary' and e['phase']=='preserve-STOP-write' for e in rt.events)
 require(error is None or allowed_outer or allowed_secondary,'UNEXPECTED_EXCEPTION',repr(error))
 if case['route']=='execute':
  module=rt.modules['execute']
  if secondary:
   require(module['preservation_attempted'] and module['terminal_transition_error']['errno']==5 and module['terminal_exit_armed'] is False,'PRESERVE_ONCE','secondary failure escapes actual outer adapter')
   require(len(fs.nodes[root+'.finalization-STOP.json']['raw'])==17 and len(fs.nodes[root+'.late-signals.jsonl']['raw'])==0,'RAW_PARTIAL','exact secondary and primary partial files retained')
   require(root+'/result.json' not in fs.nodes and popen==0 and not rt.attempts,'NO_SYNTHETIC_PRESERVATION','no result or final sample invented')
   state['executeTerminalAgreement']=whole_execute_terminal(case,state,secondary=True)
   return {'case':case['id'],'matched':True,'fakeOnly':True,'kernelOrActualAdapterWaitOrCrashProof':False,'observed':snapshot(state)}
  result=json.loads(fs.nodes[root+'/result.json']['raw'])
  require(result.get('originalFIRSTHashes',state['FIRST'])==state['FIRST'],'FIRST_PRESERVED','result binding')
  require(module['terminal_exit_armed'] is True,'ARMED_TERMINAL','whole execute')
  late=root+'.late-signals.jsonl'
  if failure in ('late-open','late-EACCES','late-fsync','late-directory'):
   require(module['preservation_attempted'] is True and module['terminal_transition_error']['errno']==(13 if failure=='late-EACCES' else 28 if failure=='late-open' else 5),'PRESERVE_ONCE','precise terminal setup error')
   stop=root+'.finalization-STOP.json';require(stop in fs.nodes,'STOP_REQUIRED','one-shot preservation STOP')
   note=json.loads(fs.nodes[stop]['raw']);require(note['terminalTransition']['preservationAttempted'] is True and note['terminalTransition']['terminalTransitionError']['lateSignalPath']==late,'STOP_REQUIRED','error+partial path+live state')
   require(sum(n==stop for n in rt.created)==1,'PRESERVE_ONCE','no reopen or retry')
   require((late in fs.nodes)==(failure in ('late-fsync','late-directory')),'RAW_REQUIRED','exact partial late path presence')
   require(root+'/SHA256SUMS' in fs.nodes,'PRESERVATION_TERMINAL','incomplete seal')
   require(popen==1 and len(rt.attempts)==1,'POPEN_COUNT','one preservation-only final sample')
   if case.get('signal_at'):require(module['signals']==[15] and any(e['phase']=='signal-late-fsync' for e in rt.events),'LIVE_SIGNAL_RED','signal survives setup error and preservation')
  else:
   require(module['signals']==[15] and code==143 and result['executorActualExit']==0,'LIVE_SIGNAL_RED','provisional original result exact')
   require(late in fs.nodes,'RAW_REQUIRED','live signal sidecar')
   if case.get('inject')=='outer-exit':require(fs.nodes[late]['raw']==b'' and any(e.get('type')=='AssertionError' and e.get('reason')=='publication parent descriptor closed' for e in module['signal_record_errors']),'LIVE_SIGNAL_RED','postclose signal red with exact closed-parent error')
   if case.get('inject')!='outer-exit':require(root+'.late-STOP.json' in fs.nodes,'STOP_REQUIRED','live finalizer signal STOP')
   require(popen==1 and len(rt.attempts)==1,'POPEN_COUNT','one finalizer sample')
   if failure=='signal-write':require(len(fs.nodes[late]['raw'])==1 and module['signal_record_errors'],'RAW_REQUIRED','exact partial signal byte and error')
  state['executeTerminalAgreement']=whole_execute_terminal(case,state)
  require(not rt.signals,'NO_FOREIGN_SIGNALS','execute forwards none')
 else:
  require(popen==(0 if setup or failure=='Popen' else 1),'POPEN_COUNT','exact checker count')
  require(len(rt.attempts)==(0 if setup else 1),'POPEN_COUNT','exact checker attempt count')
  journal=root+'.postseal.check-retention.journal'
  if failure not in ('parent-open','parent-link','parent-UID'):require(journal in fs.nodes,'JOURNAL_REQUIRED','partial or complete UNKNOWN')
  if setup:
   if journal in fs.nodes:require(b'UNCOMMITTED' in fs.nodes[journal]['raw'] or failure=='journal-short' and len(fs.nodes[journal]['raw'])==17 or failure=='parent-identity' and fs.nodes[journal]['raw']==b'','JOURNAL_REQUIRED','specific pre-capture partial UNKNOWN')
   require(not any(c[0]=='barrier-write' for c in rt.calls) and not rt.signals,'NO_CHECKER_START','failed publication precondition')
   require(not any(root+'.postseal.check-'+n+'.raw' in fs.nodes for n in ('stdout','stderr')),'NO_CAPTURE_CREDIT','pre-capture failure')
  else:
   require(b'UNCOMMITTED' in fs.nodes[journal]['raw'] and b'UNKNOWN' in fs.nodes[journal]['raw'],'JOURNAL_REQUIRED','specific UNKNOWN marker')
   for name in ('stdout','stderr'):
    path=root+'.postseal.check-'+name+'.raw'
    require(path in fs.nodes,'RAW_REQUIRED',path)
    saved=fs.nodes[path]['raw'];obtained=bytes(rt.obtained[name])
    if failure=='stdout-memory' and name=='stdout':require(saved==b'','RAW_PARTIAL','exact expected allocation-before-write')
    elif failure=='stderr-FSIZE' and name=='stderr':require(saved==obtained[:17],'RAW_PARTIAL','exact obtained partial FSIZE prefix')
    else:require(saved==obtained and digest(saved)==digest(obtained),'RAW_HASH','all obtained bytes/hash')
   terminal_path=root+'.postseal.check-terminal.json'
   if failure=='terminal-JSON-memory':
    require(terminal_path not in fs.nodes and b'CHECKER_OBSERVED_RAW_STATUS_AND_RETURN:0:0' in fs.nodes[journal]['raw'] and b'FULL_TERMINAL_UNRETAINED_UNKNOWN' in fs.nodes[journal]['raw'],'TERMINAL_UNKNOWN','specific exact observed wait fallback')
    terminal=None
   else:
    require(terminal_path in fs.nodes,'WAIT_REQUIRED','terminal artifact mandatory')
    terminal=json.loads(fs.nodes[terminal_path]['raw'])
    afterseal_terminal(rt,terminal)
    require(terminal['timeoutSeconds']==30 and terminal['noRetry'],'CAP_PRESERVED','checker30/no retry')
    unknown=checker_mode in ('wait-error','wait-unknown')
    if unknown:require(terminal['actualWait'] is None and terminal['actualReturnCode'] is None and terminal['errors'],'WAIT_UNKNOWN','exact missing wait red')
    elif failure=='Popen':require(terminal['actualWait'] is None and terminal['actualReturnCode'] is None,'WAIT_UNKNOWN','Popen never started')
    else:
     require(type(terminal['actualWait']) is dict,'WAIT_REQUIRED','observed wait mandatory')
     expected_status=-9 if terminal['ownSignalsSent'] else 0
     require(terminal['actualReturnCode']==terminal['actualWait']['actualReturnCode']==terminal['actualWait']['rawWaitStatus']==expected_status,'EXACT_WAIT','specific fake raw status')
    if checker_mode=='partial':require(terminal['eof']['stderr'] is False and not terminal['captureComplete'],'EOF_UNKNOWN','specific stderr missing EOF')
    elif failure in ('Popen','identity-UID','identity-PGID','identity-argv','identity-exe','identity-root-read','identity-stat-read','identity-status-read','identity-cmdline-read','identity-exe-read','pidfd-open','pidfd-race','selector-register'):
     require(not terminal['barrierReleased'],'BARRIER_REQUIRED','failed barrier never released')
    elif failure in FULL_CAPTURE_TAIL or checker_mode=='normal' and not failure:
     require(terminal['captureComplete'] and all(terminal['eof'].values()),'EOF_REQUIRED','full capture case')
   if failure and failure.startswith(('identity','pidfd')):require(not rt.signals,'NO_FOREIGN_SIGNALS','authority failure sends no signal')
   require(all(e['pid']==rt.pid and e['signal']==9 for e in rt.signals),'NO_FOREIGN_SIGNALS','exact owned only')
   values=[]
   for text in rt.output:
    try:value=json.loads(text)
    except ValueError:continue
    if isinstance(value,dict) and 'BS-F2' in value:values.append(value)
   no_output=failure in ('output-JSON-memory','print','print-flush')
   require(bool(values) or no_output,'OUTPUT_REQUIRED','receipt mandatory except exact reached output fault')
   if values:
    value=values[-1]
    require(value['passed'] is (code==0) or failure=='parent-close' and code==1 and any(v['passed'] is True for v in values),'PRECISE_RESULT','inner zero provisional only named close fault')
    afterseal_terminal(rt,value['checkerTerminal'])
    if terminal is not None:
     require(same_json_types_and_values(value['checkerTerminal'],terminal),'EXACT_WAIT','receipt terminal types and values equal raw terminal')
    charges=value['adjacentRetentionChargedBytes'];components=value['combinedConservativeComponentsBytes']
    metas=[value['retentionJournal']]+list(value['obtainedEvidence']['artifacts'].values())+([value['checksumOutputFallback']] if 'checksumOutputFallback' in value else [])
    require(charges==sum(m['chargedConservativeBytes'] for m in metas) and components['retainedPacketAndBundle']>=charges,'EXACT_CHARGE','every retained artifact once; parent directories common-root sampled separately')
    if failure in ('fallback-EIO','fallback-EACCES','fallback-ENOTDIR'):
     m=value['checksumOutputFallback'];require(m['allocatedBytes'] is None and m['chargedConservativeBytes']>=4096 and m['provenAbsent'] is False,'UNKNOWN_CHARGE','inspection errno preserves unknown')
    if failure=='fallback-ENOENT':require(value['checksumOutputFallback']['provenAbsent'] is True and value['checksumOutputFallback']['absenceErrno']==2,'EXACT_ABSENCE','verified parent ENOENT only')
    if failure=='disk':require(value['diskAvailableBytes']==4096 and 'shared native actual FINAL reserve/OOM0 red' in value['violations'],'EXACT_RESOURCE_ERROR','named disk reserve')
    if failure=='RSS':require(value['nativeOwnVmRSSBytes']==536870912 and 'native boundary red' in value['violations'],'EXACT_RESOURCE_ERROR','named own RSS cap')
    if failure=='component':require(components['retainedPacketAndBundle']>value['remoteFINAL']['componentsBytes']['retainedPacketAndBundle'] and 'ordinary charged retention allocation excess' in value['violations'],'EXACT_RESOURCE_ERROR','named charged component cap')
   else:
    fallback=root+'.postseal.check-output-fallback.json';require(fallback in fs.nodes,'RAW_REQUIRED','exact output error fallback mandatory')
    saved=json.loads(fs.nodes[fallback]['raw']);require(saved['codecFailure']['type']==('MemoryError' if failure=='output-JSON-memory' else 'OSError'),'EXACT_ERROR','named output fault')
   # Independently require the selected failure records, not just fixture tags.
   # A reached trigger followed by an unrelated exception must not match.
   def errors_in(value):
    rows=[]
    if isinstance(value,dict):
     if 'type' in value and 'reason' in value:rows.append(value)
     for nested in value.values():rows.extend(errors_in(nested))
    elif isinstance(value,list):
     for nested in value:rows.extend(errors_in(nested))
    return rows
   evidence_errors=errors_in(values)+errors_in(terminal) if terminal is not None else errors_in(values)
   fallback_path=root+'.postseal.check-output-fallback.json'
   if not values and fallback_path in fs.nodes:evidence_errors+=errors_in(json.loads(fs.nodes[fallback_path]['raw']))
   for phase,kind,number in FAULT_SITES.get(failure,[]):
    if kind is not None and failure!='parent-close':require(any(e['type']==kind and e.get('errno')==number for e in evidence_errors),'EXACT_ERROR','selected retained error '+str((failure,phase,kind,number)))
   assertion_messages={'identity-UID':'checker UID/root identity UNKNOWN','identity-PGID':'checker parent/session UNKNOWN','identity-argv':'checker full barrier argv mismatch','identity-exe':'checker exe identity mismatch','pidfd-race':'checker pidfd-open identity race/UNKNOWN'}
   if failure in assertion_messages:require(any(e['type']=='AssertionError' and e['reason']==assertion_messages[failure] for e in evidence_errors),'EXACT_ERROR','selected exact identity failure')
   if failure=='stream-overflow':require(any(e['type']=='OverflowError' and e['reason']=='obtained checker stream exceeds bounded raw capture; retained prefix is partial' for e in evidence_errors),'EXACT_ERROR','selected bound crossing')
   if failure=='parent-close':require(any(any(e['phase']=='publication-parent-close' and e['type']=='OSError' and e['errno']==5 for e in json.loads(text)['publicationCloseErrors']) for text in rt.diagnostics),'EXACT_ERROR','actual selected close diagnostic and outer RED')
   # Publication ordering is a per-case predicate, not a comment or healthy-case
   # surrogate. The initial journal barrier must precede the checker attempt.
   first_open=next(i for i,c in enumerate(rt.fs.log) if c[:2]==['fd-open-at',journal])
   barriers=[i for i,c in enumerate(rt.fs.log) if c[:2]==['fsync','/f']]
   require(bool(barriers) and barriers[0]>first_open,'PUBLICATION_ORDER','initial file then directory synchronization')
   checker_start=next(i for i,c in enumerate(rt.fs.log) if c[0]=='checker-attempt')
   require(barriers[0]<checker_start,'PUBLICATION_ORDER','initial publication before checker start')
 return {'case':case['id'],'matched':True,'fakeOnly':True,'kernelOrActualAdapterWaitOrCrashProof':False,'observed':snapshot(state)}

def snapshot(state):
 rt=state['rt'];root='/f/executor'
 return {'code':state['code'],'error':state['error'],'selectedStderrDiagnostics':rt.diagnostics,'events':rt.events,'calls':rt.calls,'fsLog':rt.fs.log,'PopenAttempts':rt.attempts,'ownSignalAttempts':rt.signals,'FIRST':state['FIRST'],'obtained':{k:{'bytes':len(v),'sha256':digest(v)} for k,v in rt.obtained.items()},'artifacts':{n:{'bytes':len(v['raw']),'sha256':digest(v['raw'])} for n,v in rt.fs.nodes.items() if n.startswith(root+'.') and not v['directory']},'selectedWholeModules':list(rt.modules),'selectedTerminalOutput':list(rt.output),'executeTerminalAgreement':state.get('executeTerminalAgreement'),'fixtureWaitObservations':rt.wait_observations,'fixtureIdentityObservations':rt.identity_observations,'fixtureOutputObservations':[{k:(dict(bytes=len(v),sha256=digest(v),fullRawHex=v.hex()) if type(v) is bytes else v) for k,v in observation.items()} for observation in rt.output_observations]}

def v6_case(case,on_state=None):
 if 'discriminator' not in case:
  state=exercise(case)
  if on_state is not None:on_state(state)
  return strict_oracle(case,state)
 target=dict(next(c for c in V5_CASES if c.get('failure')=='mode-EIO'));which=case['discriminator']
 if which=='unrelated':target['decoy']='unrelated-before-capture'
 state=exercise(target);rt=state['rt'];root='/f/executor'
 if on_state is not None:on_state(state)
 if which=='missing-trigger':rt.events=[]
 if which=='wrong-phase':
  for e in rt.events:
   if e['name']=='mode-EIO':e['phase']='unrelated-early-phase'
 if which=='missing-raw':del rt.fs.nodes[root+'.postseal.check-stdout.raw']
 if which=='missing-wait':
  path=root+'.postseal.check-terminal.json';value=json.loads(rt.fs.nodes[path]['raw']);value['actualWait']=None;rt.fs.json(path,value)
 if which=='unrelated-error':state['error']={'type':'MemoryError','reason':'unrelated outer error','errno':None}
 if which=='missing-output':rt.output=[]
 try:strict_oracle(target,state)
 except OracleReject as error:
  require(error.predicate==case['reject'],'DISCRIMINATOR_EXACT_REJECTION',str(error))
  return {'case':case['id'],'matched':True,'fakeOnly':True,'rejectedBy':error.predicate,'observed':snapshot(state)}
 raise OracleReject('DISCRIMINATOR_FALSE_ACCEPT',case['id'])

# Each complete evidence JSON is losslessly encoded with deterministic gzip,
# then stored in create-only chunks. No trace trimming or event selection.
# One bounded index binds exact order/offset/length/hash and actual file identity;
# concatenating and decoding its chunks gives the exact complete JSON bytes.
# All bytes, reservations, partials, files and directories retain ordinary
# common-root/component charging. The 8MiB adapter screen and 65536 per-file
# boundary are unchanged; actual filesystem/outer resource proof stays UNRUN.
OUTPUT_TOTAL_CAP=8388608
OUTPUT_RECORD_CAP=65536
EVIDENCE_CHUNK_BYTES=65536
INDEX_ENTRY_CAP=384
INDEX_HEADER_CAP=4096
def incremental_run(new_output):
 import os,signal,subprocess
 target=pathlib.Path(new_output);directory=None;directory_identity=None
 charged=0;charged_block_screen=0;signals=[];publications=[];evidence_progress=None;stop_written=False
 index=-1;case={'id':'setup'};primary_error=None
 def error_binding(error):
  reason=str(error).encode('utf-8');return {'type':type(error).__name__,'errno':getattr(error,'errno',None),'predicate':getattr(error,'predicate',None),'reasonBytes':len(reason),'reasonSha256':digest(reason)}
 def caught(number,unused):signals.append(number);raise InterruptedError('own adapter signal '+str(number))
 for number in (signal.SIGTERM,signal.SIGINT,signal.SIGHUP):signal.signal(number,caught)
 def real_identity(info):
  require(stat.S_ISDIR(info.st_mode),'OUTPUT_DIRECTORY_TYPE','nonlink directory required')
  return (info.st_uid,info.st_dev,info.st_ino,info.st_mode)
 def real_walk(path):
  parts=pathlib.PurePosixPath(str(path)).parts;flags=os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC;current=os.open('/',flags)
  try:
   require(real_identity(os.fstat(current))==real_identity(os.stat('/',follow_symlinks=False)),'OUTPUT_DIRECTORY_IDENTITY','root')
   for part in parts[1:]:
    expected=real_identity(os.stat(part,dir_fd=current,follow_symlinks=False));child=os.open(part,flags,dir_fd=current)
    try:require(real_identity(os.fstat(child))==expected==real_identity(os.stat(part,dir_fd=current,follow_symlinks=False)),'OUTPUT_DIRECTORY_IDENTITY','ancestor')
    except BaseException:os.close(child);raise
    previous=current;current=child;os.close(previous)
   return current
  except BaseException:os.close(current);raise
 def verify_output():
  require(directory is not None and real_identity(os.fstat(directory))==directory_identity,'OUTPUT_DIRECTORY_IDENTITY','bound output')
  probe=real_walk(target)
  try:require(real_identity(os.fstat(probe))==directory_identity,'OUTPUT_DIRECTORY_IDENTITY','named output')
  finally:os.close(probe)
 def emit_bytes(name,body,reserve=8192):
  nonlocal charged,charged_block_screen
  require(type(body) is bytes and 0<len(body)<=OUTPUT_RECORD_CAP,'OUTPUT_RECORD_BOUND',name)
  require('/' not in name and name not in ('.','..') and len(name.encode())<=96,'OUTPUT_CONFINEMENT','relative regular output name')
  require(charged+len(body)<=OUTPUT_TOTAL_CAP-reserve,'OUTPUT_TOTAL_BOUND',name)
  verify_output()
  # A body is reserved once before a fallible open/write. Reservation is a
  # conservative logical screen, never a claim that absent/partial names used
  # that many actual blocks. Actual samples and UNKNOWN are retained separately.
  charged+=len(body);charged_block_screen+=((len(body)+4095)//4096)*4096
  record={'name':name,'expectedBytes':len(body),'expectedSha256':digest(body),'status':'OPEN_UNCONFIRMED','actualIdentity':None,'actualBytes':None,'actualAllocatedBytes':None,'error':None}
  publications.append(record);filefd=None
  try:
   filefd=os.open(name,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW|os.O_CLOEXEC,0o600,dir_fd=directory)
   a=os.fstat(filefd);named=os.stat(name,dir_fd=directory,follow_symlinks=False)
   ident=lambda s:(s.st_uid,s.st_dev,s.st_ino,s.st_mode,s.st_nlink)
   require(stat.S_ISREG(a.st_mode) and a.st_uid==os.getuid() and a.st_nlink==1 and ident(a)==ident(named),'OUTPUT_FILE_IDENTITY',name)
   record['actualIdentity']=list(ident(a));record['status']='CREATED_UNCOMMITTED'
   written=os.write(filefd,body);record['writeReturnedBytes']=written
   require(written==len(body),'OUTPUT_SHORT_WRITE',name);os.fsync(filefd)
   b=os.fstat(filefd);named=os.stat(name,dir_fd=directory,follow_symlinks=False)
   record['actualBytes']=b.st_size;record['actualAllocatedBytes']=b.st_blocks*512
   require(ident(a)==ident(b)==ident(named) and b.st_size==len(body),'OUTPUT_FILE_IDENTITY',name)
   os.fsync(directory);verify_output();record['status']='FILE_AND_DIRECTORY_SYNCED_CLOSE_PENDING'
  except BaseException as error:
   record['error']=error_binding(error);record['status']='UNKNOWN_PARTIAL_OR_ABSENT';raise
  finally:
   if filefd is not None:
    try:os.close(filefd)
    except BaseException as error:record['closeError']=error_binding(error);record['status']='UNKNOWN_CLOSE';raise
  record['status']='PUBLISHED_API_ORDER_ONLY'
  return {'name':name,'bytes':len(body),'sha256':digest(body),'uid':a.st_uid,'dev':a.st_dev,'ino':a.st_ino,'mode':a.st_mode,'nlink':a.st_nlink,'allocatedBytes':record['actualAllocatedBytes']}
 def emit(name,value,reserve=8192):
  body=(json.dumps(value,sort_keys=True,allow_nan=False)+'\n').encode('utf-8');return emit_bytes(name,body,reserve)
 def emit_evidence(number,label,detail):
  nonlocal evidence_progress
  # Only this serialization defines the lossless representation. Its failure
  # is red before any complete-payload/index/completed claim exists.
  evidence_progress={'prefix':'%03d.%s'%(number,label),'status':'SERIALIZING','payloadBytes':None,'payloadSha256':None,'chunks':[],'index':None}
  raw=(json.dumps(detail,sort_keys=True,allow_nan=False)+'\n').encode('utf-8')
  body,codec=encode_payload(raw)
  count=(len(body)+EVIDENCE_CHUNK_BYTES-1)//EVIDENCE_CHUNK_BYTES
  require(0<count<=OUTPUT_TOTAL_CAP//EVIDENCE_CHUNK_BYTES,'OUTPUT_TOTAL_BOUND','complete evidence chunk count')
  evidence_progress.update(codec,status='PUBLISHING_CHUNKS',chunkCount=count)
  # Reserve worst bounded index plus completed/failed/STOP budgets before first
  # chunk. Every entry is separately screened before its index publication.
  require(charged+len(body)+count*INDEX_ENTRY_CAP+INDEX_HEADER_CAP+8192<=OUTPUT_TOTAL_CAP,'OUTPUT_TOTAL_BOUND','complete evidence and bounded index reservation')
  for ordinal in range(count):
   offset=ordinal*EVIDENCE_CHUNK_BYTES;chunk=body[offset:offset+EVIDENCE_CHUNK_BYTES]
   binding=emit_bytes('%03d.%s.chunk-%03d.raw'%(number,label,ordinal),chunk)
   entry=dict(binding,ordinal=ordinal,offset=offset)
   require(len(json.dumps(entry,sort_keys=True,allow_nan=False).encode())+2<=INDEX_ENTRY_CAP,'OUTPUT_INDEX_ENTRY_BOUND','identity/path/order entry')
   evidence_progress['chunks'].append(entry)
  manifest=dict(codec,schema=SCHEMA,directory=str(target),directoryIdentity=list(directory_identity),caseIndex=number,label=label,chunkBytesMaximum=EVIDENCE_CHUNK_BYTES,chunkCount=count,chunks=list(evidence_progress['chunks']),join='ordinal0..count-1, exact offsets/lengths/hashes/identities; concatenate encoded bytes; encoded length/hash; bounded gzip decode; full payload length/hash; then UTF8 JSON',noDroppedTrace=True,runtimeCrashGuarantee='UNRUN; verified API ordering only')
  header=dict(manifest,chunks=[])
  require(len(json.dumps(header,sort_keys=True,allow_nan=False).encode())+1<=INDEX_HEADER_CAP,'OUTPUT_INDEX_HEADER_BOUND','exact directory and full-payload index')
  bound=emit('%03d.%s.index.json'%(number,label),manifest)
  evidence_progress.update(status='COMPLETE_API_PUBLISHED',index=bound)
  return bound
 def stop(error,primary=None):
  nonlocal stop_written
  if stop_written:return
  stop_written=True
  # Compact STOP binds the full reason and partial publication bytes, rather
  # than copying an unbounded trace into another 65536-byte record. It gives no
  # completed credit; exact traces remain in their chunks or the failed payload.
  last=publications[-1] if publications else None
  emit('FIRST-STOP.json',{'index':index,'case':case['id'],'status':'FAILED','matched':False,'error':error_binding(error),'primaryError':error_binding(primary) if primary is not None else None,'actualExitIntended':1,'noRetry':True,'reservedLogicalBytes':charged,'reservedBlockScreenBytes':charged_block_screen,'lastPublication':last,'evidenceProgress':{k:v for k,v in (evidence_progress or {}).items() if k!='chunks'},'publishedChunkCount':len((evidence_progress or {}).get('chunks',[])),'signals':signals,'partialNamesMustBeChargedByActualRootFinal':True,'filesystemCrashSurvival':'UNRUN'},reserve=0)
 try:
  own_pid=os.getpid();own_locks=[s for s in pathlib.Path('/proc/locks').read_text().splitlines() if s.split()[4]==str(own_pid)]
  print(json.dumps({'BS-F2':'own future control adapter start','hostname':subprocess.check_output(['hostname'],text=True).strip(),'ownPid':own_pid,'ownLockLines':own_locks,'dfB1':subprocess.check_output(['df','-B1','/'],text=True),'utc':subprocess.check_output(['/usr/bin/date','-u','+%Y-%m-%dT%H:%M:%S.%NZ'],text=True).strip(),'actualRuntimeAttribution':'UNKNOWN; bind actual adapter separately'}),flush=True)
  require(target.is_absolute() and '..' not in target.parts and str(pathlib.PurePosixPath(str(target)))==str(target),'OUTPUT_CONFINEMENT','absolute exact nonlink output path')
  parent=real_walk(target.parent)
  try:
   require(os.fstat(parent).st_uid==os.getuid(),'OUTPUT_DIRECTORY_UID','own parent')
   os.mkdir(target.name,mode=0o700,dir_fd=parent);directory=real_walk(target);directory_identity=real_identity(os.fstat(directory))
   require(directory_identity[0]==os.getuid(),'OUTPUT_DIRECTORY_UID','own output');os.fsync(parent)
  finally:os.close(parent)
  verify_output()
  for index,case in enumerate(V7_CASES):
   state=None;evidence_progress=None
   def remember_state(value):
    nonlocal state
    state=value
   try:
    if 'discriminator' in case or 'wholeDiscriminator' in case:row=v7_case(case,on_state=remember_state)
    else:state=exercise(case);row=strict_oracle(case,state)
    detail=row.pop('observed');bound=emit_evidence(index,'evidence',detail)
    emit('%03d.completed.json'%index,dict(row,evidenceIndex=bound,status='COMPLETED',actualAdapterWaitAndDurabilityStillRequired=True))
   except BaseException as error:
    primary_error=error
    try:
     emit('%03d.failed.json'%index,{'case':case['id'],'status':'FAILED','matched':False,'error':error_binding(error),'evidenceProgress':{k:v for k,v in (evidence_progress or {}).items() if k!='chunks'}},reserve=4096)
     if state is not None:emit_evidence(index,'failed-evidence',dict(snapshot(state),adapterPrimaryError={'binding':error_binding(error),'fullReason':str(error)},adapterPublications=list(publications)))
    except BaseException as secondary:
     stop(secondary,primary=error);raise
    stop(error);return 1
  emit('completed-summary.json',{'count':len(V7_CASES),'datedOriginal128AndCurrent66RemainUNRUNUntilThisDistinctGateActuallyRuns':True,'actualKernelAdapterWaitDurabilityAndRootFinalRemainRequired':True,'fakeOnly':True,'logicalBytesReserved':charged,'blockScreenBytesReserved':charged_block_screen})
  return 0
 except BaseException as error:
  if directory is not None:stop(error,primary=primary_error)
  # Setup before output binding cannot promise a durable STOP. Actual outer
  # exception remains nonzero, and the separate adapter must retain it.
  raise
 finally:
  if directory is not None:os.close(directory)

if __name__=='__main__':
 parser=argparse.ArgumentParser();parser.add_argument('--new-output',required=True);args=parser.parse_args()
 raise SystemExit(incremental_run(args.new_output))
