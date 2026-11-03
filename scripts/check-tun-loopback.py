import socket,struct,json,subprocess,threading,time,queue,ipaddress,os
from pathlib import Path
import argparse
parser=argparse.ArgumentParser(description='Loopback-only Xray DNS/UDP check; never creates a TUN device.')
parser.add_argument('--xray',required=True)
parser.add_argument('--fixture',required=True)
parser.add_argument('--assets',required=True)
parser.add_argument('--work',required=True)
args=parser.parse_args()
root=Path(args.work);root.mkdir(parents=True,exist_ok=True)
fixture=Path(args.fixture)
xray=args.xray
seen=queue.Queue()
def free():
 with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
def read(s,n):
 b=b''
 while len(b)<n:
  c=s.recv(n-len(b))
  if not c:raise EOFError()
  b+=c
 return b
def address(s,kind):
 if kind==3:return read(s,read(s,1)[0]).decode()
 return socket.inet_ntop(socket.AF_INET if kind==1 else socket.AF_INET6,read(s,4 if kind==1 else 16))
class Mock:
 def __init__(self,name):
  self.name=name;self.stop=False;self.t=socket.socket();self.t.bind(('127.0.0.1',0));self.port=self.t.getsockname()[1];self.t.listen();self.t.settimeout(.2)
  self.u=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);self.u.bind(('127.0.0.1',0));self.u.settimeout(.2)
  threading.Thread(target=self.accept,daemon=True).start();threading.Thread(target=self.udp,daemon=True).start()
 def accept(self):
  while not self.stop:
   try:c,_=self.t.accept()
   except (TimeoutError,OSError):continue
   threading.Thread(target=self.serve,args=(c,),daemon=True).start()
 def serve(self,c):
  with c:
   c.settimeout(5)
   try:
    v,n=read(c,2);read(c,n);c.sendall(b'\x05\x00');h=read(c,4);host=address(c,h[3]);port=struct.unpack('!H',read(c,2))[0]
    replyport=self.u.getsockname()[1] if h[1]==3 else 0
    c.sendall(b'\x05\x00\x00\x01\x7f\x00\x00\x01'+struct.pack('!H',replyport))
    if h[1]==1:
     seen.put((self.name,'tcp',host,port))
     while True:
      data=c.recv(65535)
      if not data:break
      c.sendall(data)
    else:
     while c.recv(1):pass
   except (OSError,EOFError):pass
 def udp(self):
  while not self.stop:
   try:data,peer=self.u.recvfrom(65535)
   except (TimeoutError,OSError):continue
   seen.put((self.name,'udp'));self.u.sendto(data,peer)
 def close(self):self.stop=True;self.t.close();self.u.close()
default=Mock('default');dedicated=Mock('dedicated')
c=json.loads(fixture.read_text(encoding='utf-8-sig'))
mixed=free();dnsport=free()
c['inbounds']=[i for i in c['inbounds'] if i.get('protocol')!='tun']
for i in c['inbounds']:
 i['port']=free();i['listen']='127.0.0.1'
 if i['tag']=='socks':i['port']=mixed;i['settings']['udp']=True
c['inbounds'].append({'tag':'dns-test','listen':'127.0.0.1','port':dnsport,'protocol':'dokodemo-door','settings':{'address':'1.1.1.1','port':53,'network':'tcp,udp'}})
for o in c['outbounds']:
 if o['tag'] in ('proxy','ai-policy'):
  o['protocol']='socks';o['settings']={'servers':[{'address':'127.0.0.1','port':default.port if o['tag']=='proxy' else dedicated.port}]};o.pop('streamSettings',None)
c['api']['listen']='127.0.0.1:'+str(free())
c['log']={'loglevel':'warning'}
path=root/'tun-loopback-fixture.json';path.write_text(json.dumps(c),encoding='utf-8')
env=os.environ.copy();env['XRAY_LOCATION_ASSET']=args.assets
subprocess.run([xray,'run','-test','-config',str(path)],env=env,check=True)
def dns(name,typ=1):
 q=b'\x12\x34\x01\x00\x00\x01\x00\x00\x00\x00\x00\x00'+b''.join(bytes([len(x)])+x.encode() for x in name.split('.'))+b'\x00'+struct.pack('!HH',typ,1)
 with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as s:
  s.settimeout(5);s.sendto(q,('127.0.0.1',dnsport));data=s.recv(4096)
 assert data[:2]==q[:2]
 answers=struct.unpack('!H',data[6:8])[0]
 if typ==65:assert answers==0;return None
 assert answers>=1
 return str(ipaddress.ip_address(data[-(4 if typ==1 else 16):]))
def connect(host,port):
 s=socket.create_connection(('127.0.0.1',mixed),timeout=5);s.sendall(b'\x05\x01\x00');assert read(s,2)==b'\x05\x00'
 try:ip=ipaddress.ip_address(host);a=bytes([1 if ip.version==4 else 4])+ip.packed
 except ValueError:a=b'\x03'+bytes([len(host)])+host.encode()
 s.sendall(b'\x05\x01\x00'+a+struct.pack('!H',port));h=read(s,4);assert h[1]==0;address(s,h[3]);read(s,2)
 s.sendall(b'GET / HTTP/1.1\r\nHost: '+host.encode()+b'\r\n\r\n');s.recv(4096);s.close()
def expect(role,proto,host=None):
 end=time.time()+5
 while time.time()<end:
  e=seen.get(timeout=5)
  if e[0]==role and e[1]==proto and (host is None or e[2]==host):return
 raise AssertionError((role,proto,host))
with (root/'tun-loopback.log').open('w',encoding='utf-8') as log:
 p=subprocess.Popen([xray,'run','-config',str(path)],env=env,stdout=log,stderr=log,creationflags=getattr(subprocess,"CREATE_NO_WINDOW",0))
 try:
  time.sleep(1)
  for host in ['pingip.co','cleanip.io','ip.net.coffee','yahoo.com']:
   fake=dns(host);assert ipaddress.ip_address(fake) in ipaddress.ip_network('198.18.0.0/15')
   connect(fake,443);expect('default','tcp',host)
   print('PASS FakeDNS restores '+host+' and keeps manual default exit',flush=True)
  fake=dns('api.openai.com');connect(fake,443);expect('dedicated','tcp','api.openai.com')
  fake=dns('known-proxy.example');connect(fake,443);expect('dedicated','tcp','known-proxy.example')
  print('PASS AI and community domains use independent dedicated exit',flush=True)
  fake6=dns('yahoo.com',28);assert ':' in fake6;connect(fake6,443);expect('default','tcp','yahoo.com')
  dns('query-type65.example',65);print('PASS IPv6 FakeDNS and non-address query containment',flush=True)
  with socket.create_connection(('127.0.0.1',mixed),timeout=5) as tcp:
   tcp.sendall(b'\x05\x01\x00');read(tcp,2);tcp.sendall(b'\x05\x03\x00\x01'+b'\x00'*6);h=read(tcp,4);a=address(tcp,h[3]);port=struct.unpack('!H',read(tcp,2))[0]
   with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as udp:
    udp.settimeout(5);packet=b'\x00\x00\x00\x01'+socket.inet_aton('203.0.113.7')+struct.pack('!H',3478)+b'udp-relay-test'
    udp.sendto(packet,('127.0.0.1',port));reply=udp.recv(4096);assert reply.endswith(b'udp-relay-test');expect('default','udp')
  print('PASS UDP/STUN-port payload is proxied and returned, not blocked or sent direct',flush=True)
 finally:
  p.terminate();p.wait(timeout=8);default.close();dedicated.close()
print('No TUN interface or OS/browser settings changed.',flush=True)
