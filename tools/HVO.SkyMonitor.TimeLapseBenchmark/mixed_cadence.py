#!/usr/bin/env python3
"""Synthetic protocol check only: production arguments, mixed cadence, daily stream copy."""
import hashlib,json,os,subprocess,sys
from pathlib import Path
root=Path(sys.argv[1]);root.mkdir(parents=True,exist_ok=False)
profiles={"Software":["-c:v","libx264","-preset","veryfast","-crf","23","-x264-params","lookahead_threads=1:threads=1:fps=60/1"],"Nvidia":["-c:v","h264_nvenc","-preset","p6","-tune","hq","-rc","constqp","-qp","18"]}
cpu=str(min(os.sched_getaffinity(0)))
def run(args,wd):
 command=['timeout','90','prlimit','--as=34359738368','--cpu=60','--fsize=104857600','--nofile=128','taskset','-c',cpu,'nice','-n','19','ionice','-c','3',*args]
 return subprocess.run(command,cwd=wd,check=True,stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=95).stdout

def proof(path):
 result=json.loads(run(['ffprobe','-v','error','-threads','1','-show_streams','-show_format','-show_packets','-show_data_hash','sha256','-show_entries','packet=pts,dts,duration,data_hash:stream=codec_name,codec_type,width,height,pix_fmt,time_base,profile,level,extradata_hash:format=format_name,duration','-of','json',path.name],path.parent))
 run(['ffmpeg','-v','error','-xerror','-nostdin','-threads','1','-i',path.name,'-f','null','-'],path.parent)
 return result
report={"scope":"Synthetic red/green/blue 512x512 protocol correctness canary, not VirtualSky performance","results":[]}
for profile,settings in profiles.items():
 folder=root/profile;folder.mkdir()
 proofs=[]
 for case,(count,step) in enumerate([(3,20),(12,5)]):
  work=folder/str(case);work.mkdir();lines=['ffconcat version 1.0'];expected=[]
  for i in range(count):
   name=f'{i:05}.jpg';rgb=bytes(220 if j==i%3 else 0 for j in range(3));(work/name).write_bytes(b'P6\n512 512\n255\n'+rgb*(512*512))
   start=round(i*step*1e6/180);end=round((i+1)*step*1e6/180);duration=end-start
   lines += [f'file {name}','option framerate 1000000',f'duration {duration/1e6:.6f}'];expected.append((start,duration))
  (work/'frames.ffconcat').write_text('\n'.join(lines)+'\n')
  args=['ffmpeg','-hide_banner','-v','error','-xerror','-nostdin','-n','-threads','1','-filter_threads','1','-filter_complex_threads','1','-protocol_whitelist','file,pipe','-f','concat','-safe','0','-i','frames.ffconcat','-map','0:v:0','-an','-sn','-dn','-map_metadata','-1','-vf','scale=512:512:flags=lanczos,setsar=1',*settings,'-pix_fmt','yuv420p','-threads','1','-bf','0','-fps_mode','vfr','-enc_time_base','1:1000000','-video_track_timescale','1000000','-movie_timescale','1000000','-movflags','+faststart','-bsf:v',f'setts=duration=if(eq(N\\,{count-1})\\,{expected[-1][1]}\\,DURATION)','video.mp4']
  run(args,work);p=proof(work/'video.mp4');assert [(x['pts'],x['duration']) for x in p['packets']]==expected
  (work/'proof.json').write_text(json.dumps(p,indent=2)+'\n');(work/'command.json').write_text(json.dumps(args,indent=2)+'\n');proofs.append(p)
 assert proofs[0]['streams']==proofs[1]['streams'],json.dumps(proofs)
 (folder/'segments.ffconcat').write_text('ffconcat version 1.0\nfile 0/video.mp4\nduration 0.333333\nfile 1/video.mp4\nduration 0.333333\n')
 run(['ffmpeg','-hide_banner','-v','error','-xerror','-nostdin','-n','-threads','1','-f','concat','-safe','1','-auto_convert','0','-i','segments.ffconcat','-map','0:v:0','-an','-c:v','copy','-video_track_timescale','1000000','-movie_timescale','1000000','-movflags','+faststart','video.mp4'],folder)
 joined=proof(folder/'video.mp4');expected=[(x['pts']+333333*i,x['duration'],x['data_hash']) for i,p in enumerate(proofs) for x in p['packets']];assert [(x['pts'],x['duration'],x['data_hash']) for x in joined['packets']]==expected
 assert abs(float(joined['format']['duration'])-.666666)<.000002
 (folder/'proof.json').write_text(json.dumps(joined,indent=2)+'\n')
 report['results'].append({'profile':profile,'passed':True,'packets':len(expected),'durationMicroseconds':666666,'stream':proofs[0]['streams'][0],'payloadSha256':hashlib.sha256((folder/'video.mp4').read_bytes()).hexdigest()})
(root/'result.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report))
