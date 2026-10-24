"""Create-only publication ordering; filesystem/kernel crash qualification is UNRUN.

File fsync, verified containing-directory fsync, identity checks and closes are
required before a process-local publication flag. This records API completion,
not a measured hardware/filesystem crash-survival guarantee. No write retry.
"""
import os,pathlib,stat

def directory_identity(info):
 assert stat.S_ISDIR(info.st_mode),'publication parent type UNKNOWN'
 return (info.st_uid,info.st_dev,info.st_ino,info.st_mode)

def _walk_parent(parent):
 """Fresh descriptor walk: every ancestor is nonlink and identity checked."""
 text=str(parent);parts=pathlib.PurePosixPath(text).parts
 assert text.startswith('/') and '..' not in parts and str(pathlib.PurePosixPath(text))==text,'publication parent confinement UNKNOWN'
 flags=os.O_RDONLY|os.O_DIRECTORY|os.O_CLOEXEC|os.O_NOFOLLOW
 fd=os.open('/',flags)
 try:
  assert directory_identity(os.fstat(fd))==directory_identity(os.stat('/',follow_symlinks=False)),'publication root identity UNKNOWN'
  for name in parts[1:]:
   expected=os.stat(name,dir_fd=fd,follow_symlinks=False)
   directory_identity(expected)
   child=os.open(name,flags,dir_fd=fd)
   try:
    assert directory_identity(os.fstat(child))==directory_identity(expected)==directory_identity(os.stat(name,dir_fd=fd,follow_symlinks=False)),'publication ancestor identity UNKNOWN'
   except BaseException:
    os.close(child);raise
   previous=fd;fd=child;os.close(previous)
  return fd
 except BaseException:
  os.close(fd);raise

def bind_parent(path):
 path=pathlib.Path(path);expected=directory_identity(path.parent.lstat())
 assert expected[0]==os.getuid(),'publication parent expected UID UNKNOWN'
 fd=_walk_parent(path.parent)
 try:
  assert directory_identity(os.fstat(fd))==expected,'publication parent stable identity UNKNOWN'
  return {'fd':fd,'path':str(path.parent),'identity':expected,'closed':False,'publicationSemantics':'file-fsync+verified-directory-fsync+identity+close; actual crash guarantee UNRUN'}
 except BaseException:
  os.close(fd);raise

def verify_parent(binding):
 assert not binding['closed'],'publication parent descriptor closed'
 assert directory_identity(os.fstat(binding['fd']))==binding['identity'],'publication bound parent identity UNKNOWN'
 probe=_walk_parent(pathlib.Path(binding['path']))
 try:
  assert directory_identity(os.fstat(probe))==binding['identity'],'publication named parent identity UNKNOWN'
 finally:os.close(probe)

def parent_barrier(binding):
 verify_parent(binding)
 os.fsync(binding['fd'])
 verify_parent(binding)

def create_file(binding,path):
 path=pathlib.Path(path);assert str(path.parent)==binding['path'],'publication leaf confinement UNKNOWN'
 name=path.name;assert name and name not in ('.','..') and '/' not in name
 verify_parent(binding)
 fd=os.open(name,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_CLOEXEC|os.O_NOFOLLOW,0o600,dir_fd=binding['fd'])
 try:
  a=os.fstat(fd);b=os.stat(name,dir_fd=binding['fd'],follow_symlinks=False)
  assert stat.S_ISREG(a.st_mode) and a.st_uid==os.getuid() and a.st_nlink==1,'publication file type/UID/links UNKNOWN'
  assert (a.st_uid,a.st_dev,a.st_ino,a.st_mode,a.st_nlink)==(b.st_uid,b.st_dev,b.st_ino,b.st_mode,b.st_nlink),'publication file stable identity UNKNOWN'
  return fd
 except BaseException:
  os.close(fd);raise

def file_identity(info):
 assert stat.S_ISREG(info.st_mode) and info.st_uid==os.getuid() and info.st_nlink==1,'publication file type/UID/links UNKNOWN'
 return (info.st_uid,info.st_dev,info.st_ino,info.st_mode,info.st_nlink)

def verify_file(binding,path,fd,expected=None):
 path=pathlib.Path(path);assert str(path.parent)==binding['path'],'publication file parent confinement UNKNOWN'
 verify_parent(binding)
 actual=file_identity(os.fstat(fd));named=file_identity(os.stat(path.name,dir_fd=binding['fd'],follow_symlinks=False))
 assert actual==named and (expected is None or actual==expected),'publication file identity changed/UNKNOWN'
 return actual

def publish_bytes(binding,path,raw):
 assert type(raw) in (bytes,bytearray),'publication bytes required'
 fd=create_file(binding,path)
 try:
  expected=verify_file(binding,path,fd)
  assert os.write(fd,raw)==len(raw),'publication short write; original partial bytes retained'
  os.fsync(fd)
  assert os.fstat(fd).st_size==len(raw),'publication file size changed/UNKNOWN'
  verify_file(binding,path,fd,expected)
  parent_barrier(binding)
  verify_file(binding,path,fd,expected)
 finally:os.close(fd)
 # A close error above prevents the caller from declaring FULL_COMMITTED.

def close_parent(binding):
 assert not binding['closed'],'publication parent close already attempted; no retry'
 binding['closed']=True
 os.close(binding['fd'])
