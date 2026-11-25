#!/usr/bin/env python3
"""Install PNC Framework for the Windows Mono game from native Linux."""
import argparse, base64, datetime as dt, hashlib, io, json, os, re, shutil, stat, subprocess, sys, tempfile, urllib.request, urllib.parse, uuid, xml.etree.ElementTree as ET, zipfile
from pathlib import Path

BASE="https://github.com/asbeklfcp/pnc-framework/releases/download/"
BASE_URL=BASE+"v0.1.0/PNC-Framework-v0.1.0-PNC0.4.2-win64.zip"
BASE_HASH="187ea01afedad64b75bcfe453f207a6a051567880c5872afdc0e92341587b2d9"
LOADER_URL="https://builds.bepinex.dev/projects/bepinex_be/785/BepInEx-Unity.Mono-win-x64-6.0.0-be.785%2B6abdba4.zip"
LOADER_HASH="db430f14d6661eb38ba96fcc13c07a163e87e553710821d87e5129f915a1b26b"
FEED="https://raw.githubusercontent.com/asbeklfcp/pnc-framework/main/updates/stable/latest.xml"
GAME_HASH="86b559f74066d0013f32cffd9e38f5a5749c356f5bc29f761c1656ffe39433ce"
DLLS={"PncModManager.dll","PncEdi.dll","PncControls.dll","PncCustomEnemies.dll"}
PLUGINS="BepInEx/plugins/"
UPDATER="BepInEx/framework-updater/"
RECEIPT="BepInEx/framework-updates/installed-release.xml"
MAX=32*1024*1024

def require(ok,message):
    if not ok: raise ValueError(message)
def sha(b): return hashlib.sha256(b).hexdigest()
def hashfile(p): return sha(p.read_bytes())
def ishash(h): return bool(re.fullmatch("[a-f0-9]{64}",h or ""))

class Redirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self,req,fp,code,msg,headers,newurl):
        u=urllib.parse.urlsplit(newurl)
        require(u.scheme=="https" and not u.username and not u.password and u.port in (None,443) and u.hostname in {"github.com","release-assets.githubusercontent.com","objects.githubusercontent.com","builds.bepinex.dev","raw.githubusercontent.com"},"Unexpected redirect")
        return super().redirect_request(req,fp,code,msg,headers,newurl)

def download(url,maximum=MAX):
    print("Downloading",url.rsplit("/",1)[-1],flush=True)
    with urllib.request.build_opener(Redirects()).open(urllib.request.Request(url,headers={"User-Agent":"PNC-Linux-Installer"}),timeout=30) as f:
        b=f.read(maximum+1)
    require(len(b)<=maximum,"Download too large")
    return b

def xml(b):
    require(len(b)<=256*1024 and b"<!DOCTYPE" not in b.upper() and b"<!ENTITY" not in b.upper(),"Unsafe XML")
    return ET.fromstring(b)
def der(tag,b):
    n=len(b)
    length=bytes([n]) if n<128 else bytes([128+(n.bit_length()+7)//8])+n.to_bytes((n.bit_length()+7)//8,"big")
    return bytes([tag])+length+b
def integer(b):
    b=b.lstrip(b"\0") or b"\0"
    return der(2,(b"\0" if b[0]&128 else b"")+b)

def signed(envelope,key):
    outer=xml(envelope)
    require(outer.tag=="signedRelease","Invalid envelope")
    p=base64.b64decode("".join(outer.findtext("payload").split()),validate=True)
    sig=base64.b64decode("".join(outer.findtext("signature").split()),validate=True)
    k=xml(key)
    require(k.tag=="RSAKeyValue","Invalid public key")
    rsa=der(48,integer(base64.b64decode(k.findtext("Modulus")))+integer(base64.b64decode(k.findtext("Exponent"))))
    spki=der(48,bytes.fromhex("300d06092a864886f70d0101010500")+der(3,b"\0"+rsa))
    pem=b"-----BEGIN PUBLIC KEY-----\n"+base64.encodebytes(spki)+b"-----END PUBLIC KEY-----\n"
    with tempfile.TemporaryDirectory(prefix="pnc-verify-") as t:
        d=Path(t)
        for n,b in [("key.pem",pem),("payload",p),("signature",sig)]: (d/n).write_bytes(b)
        r=subprocess.run(["openssl","dgst","-sha256","-verify",str(d/"key.pem"),"-signature",str(d/"signature"),str(d/"payload")],capture_output=True)
        require(r.returncode==0,"Invalid publisher signature")
    return xml(p)
def version(v):
    require(bool(re.fullmatch(r"\d+\.\d+(?:\.\d+){0,2}",v or "")),"Invalid release version")
    p=tuple(map(int,v.split(".")))
    return p+(0,)*(4-len(p))
def inventory(node):
    require(node is not None,"Missing inventory")
    result={}
    for f in node.findall("file"):
        n,h=f.get("name"),f.get("sha256")
        require(n in DLLS and n not in result and ishash(h),"Invalid inventory")
        result[n]=h
    require(set(result)==DLLS,"Incomplete inventory")
    return result
def release(env,key,offer=True):
    e=signed(env,key)
    require(e.tag=="release" and e.get("schema")=="1" and e.get("approved")=="true" and e.get("channel")=="stable" and e.get("gameHash")==GAME_HASH,"Unsupported signed release")
    version(e.get("id"))
    if offer:
        now=dt.datetime.now(dt.timezone.utc)
        start=dt.datetime.fromisoformat(e.get("publishedUtc").replace("Z","+00:00"))
        end=dt.datetime.fromisoformat(e.get("expiresUtc").replace("Z","+00:00"))
        require(start<=now+dt.timedelta(minutes=5) and start<end and now<end,"Release offer expired or not yet valid")
    return e,inventory(e.find("inventory"))
def safe_rel(n):
    return bool(n and not n.startswith("/") and "\\" not in n and ":" not in n and all(x not in ("",".","..") for x in n.split("/")))
def unzip(b):
    result={};seen=set()
    with zipfile.ZipFile(io.BytesIO(b)) as z:
        for i in z.infolist():
            n=i.filename.rstrip("/")
            require(safe_rel(n) and n.casefold() not in seen and not stat.S_ISLNK(i.external_attr>>16),"Unsafe or duplicate ZIP entry")
            seen.add(n.casefold())
            if not i.is_dir():
                require(i.file_size<=MAX,"ZIP entry too large")
                result[n]=z.read(i)
    return result
def baseline(b):
    require(sha(b)==BASE_HASH,"Base installer checksum mismatch")
    z=unzip(b);m=json.loads(z["package-manifest.json"])
    require(m["version"]=="0.1.0" and m["gameHash"]==GAME_HASH,"Wrong base installer")
    result={}
    for f in m["files"]:
        n=f["path"]
        require(re.fullmatch(r"BepInEx/(plugins/[^/]+\.dll|framework-updater/[A-Za-z0-9_./-]+|config/[^/]+\.cfg)",n) and safe_rel(n) and n not in result,"Unsafe base payload")
        data=z["payload/"+n]
        require(len(data)==f["bytes"] and sha(data)==f["sha256"],"Base file mismatch")
        result[n]=(data,f)
    return result
def loader(b):
    require(sha(b)==LOADER_HASH,"BepInEx checksum mismatch")
    z=unzip(b)
    require(all(re.fullmatch(r"BepInEx/(core|patchers|plugins)/[A-Za-z0-9_.+/-]+|changelog\.txt|doorstop_config\.ini|winhttp\.dll|\.doorstop_version",n) for n in z),"Unsafe loader payload")
    return z
def package_url(e):
    v=e.get("id")
    url=BASE+f"v{v}/PNC-Framework-v{v}-PNC0.4.2-win64.update.zip"
    require(e.find("package").get("url")==url,"Unapproved package URL")
    return url
def payload(e,inv,b):
    package_url(e);p=e.find("package")
    require(0<len(b)<=MAX and len(b)==int(p.get("bytes")) and sha(b)==p.get("sha256"),"Update ZIP integrity mismatch")
    specs={}
    for f in e.find("files"):
        n=f.get("name");length=int(f.get("bytes"))
        require(n in DLLS and n not in specs and f.get("assembly")==n[:-4] and f.get("sha256")==inv[n] and 0<length<=MAX,"Invalid signed file metadata")
        version(f.get("version"));specs[n]=length
    z=unzip(b)
    require(set(z)==set(specs) and bool(z),"Unexpected update entries")
    require(all(len(b)==specs[n] and sha(b)==inv[n] for n,b in z.items()),"Update DLL mismatch")
    return z


def target(root,rel):
    require(safe_rel(rel),"Unsafe target")
    p=root
    for part in rel.split("/"):
        p=p/part
        require(not p.is_symlink() and not getattr(p,"is_junction",lambda:False)(),"Linked target refused: "+str(p))
    require(p.resolve().is_relative_to(root),"Target escapes game directory")
    return p

def closed():
    require(sys.platform.startswith("linux"),"Run on native Linux, not inside Proton")
    for p in Path("/proc").iterdir():
        if not p.name.isdigit() or int(p.name)==os.getpid():continue
        try:
            cmd=(p/"cmdline").read_bytes().lower()
            comm=(p/"comm").read_bytes().lower()
        except OSError:continue
        if b"post nut calamity.exe" in cmd or b"post nut calami" in comm:
            raise ValueError("Close Post Nut Calamity before installing.")

def atomic(p,b):
    p.parent.mkdir(parents=True,exist_ok=True)
    fd,name=tempfile.mkstemp(prefix=".pnc-stage-",dir=p.parent)
    try:
        with os.fdopen(fd,"wb") as f:
            f.write(b);f.flush();os.fsync(f.fileno())
        os.replace(name,p)
    finally:
        if os.path.exists(name):os.unlink(name)

def transaction(root,plan,expected):
    if not plan:print("Already up to date.");return
    closed()
    backup=target(root,"BepInEx/framework-install-backups/"+dt.datetime.now(dt.timezone.utc).strftime("%Y%m%d-%H%M%S")+"-"+uuid.uuid4().hex)
    backup.mkdir(parents=True);journal=[]
    for rel,b in plan.items():
        p=target(root,rel);old=p.read_bytes() if p.exists() else None
        require((sha(old) if old is not None else None)==expected[rel],"File changed during preparation")
        if old is not None:
            atomic(backup/rel,old)
            require(hashfile(backup/rel)==expected[rel],"Backup verification failed")
        journal.append({"path":rel,"old":expected[rel],"new":sha(b)})
    def record(state):atomic(backup/"journal.json",json.dumps({"state":state,"files":journal},indent=2).encode())
    record("prepared");applied=[]
    try:
        for rel,b in plan.items():
            closed()
            p=target(root,rel)
            require((hashfile(p) if p.exists() else None)==expected[rel],"File changed during installation")
            atomic(p,b);applied.append(rel)
            require(hashfile(p)==sha(b),"Installed file verification failed")
        record("complete")
    except BaseException:
        for rel in reversed(applied):
            p=target(root,rel)
            require(hashfile(p)==sha(plan[rel]),"File changed during rollback; backup retained at "+str(backup))
            if expected[rel] is None:p.unlink()
            else:atomic(p,(backup/rel).read_bytes())
        record("rolled-back");raise
    print("Verified backup:",backup)

def validate_game(root):
    require(target(root,"Post Nut Calamity.exe").is_file(),"Choose the game folder containing Post Nut Calamity.exe")
    require(hashfile(target(root,"Post Nut Calamity_Data/Managed/Assembly-CSharp.dll"))==GAME_HASH,"Wrong PNC build; requires verified 0.4.2")
    closed()

def install(root,base_data,loader_data,envelope,update_data):
    root=root.resolve(strict=True);validate_game(root)
    require(not target(root,"BepInEx/framework-updates/pending").exists(),"Finish or cancel the pending game update first")
    base=baseline(base_data);ld=loader(loader_data)
    key=base[UPDATER+"Trust.xml"][0]
    b=signed(base[UPDATER+"baseline-inventory.xml"][0],key)
    require(b.tag=="inventory" and b.get("id")=="0.1.0" and b.get("channel")=="stable" and b.get("gameHash")==GAME_HASH,"Invalid baseline")
    inv0=inventory(b)
    require(all(sha(base[PLUGINS+n][0])==h for n,h in inv0.items()),"Baseline inventory mismatch")
    e,inv=release(envelope,key);new=payload(e,inv,update_data)
    plan={};expected={}
    def add(rel,b):
        p=target(root,rel);old=hashfile(p) if p.exists() else None
        if old!=sha(b):plan[rel]=b;expected[rel]=old
    required=["BepInEx/core/BepInEx.Unity.Mono.dll","winhttp.dll","doorstop_config.ini"]
    require(all(n in ld for n in required),"Incomplete loader")
    present=[target(root,n).exists() for n in required]
    if any(present):
        require(all(present),"Partial BepInEx installation")
        for n in required[:2]:require(hashfile(target(root,n))==sha(ld[n]),"Existing loader differs from tested BepInEx")
    else:
        require(not target(root,"BepInEx/core").exists() and not target(root,".doorstop_version").exists(),"Partial BepInEx installation")
        for n,b in ld.items():
            require(not target(root,n).exists(),"Loader would overwrite "+n);add(n,b)
    existing={n:hashfile(target(root,PLUGINS+n)) for n in DLLS if target(root,PLUGINS+n).exists()}
    receipt=target(root,RECEIPT)
    if existing:
        require(set(existing)==DLLS,"Incomplete existing framework")
        for n in ("Trust.xml","baseline-inventory.xml"):
            require(hashfile(target(root,UPDATER+n))==sha(base[UPDATER+n][0]),"Existing trust files differ")
        if receipt.exists():
            old,oldinv=release(receipt.read_bytes(),key,False);installed=old.get("id")
        else:installed,oldinv="0.1.0",inv0
        require(existing==oldinv,"Existing DLLs differ from authenticated inventory")
        require(version(e.get("id"))>=version(installed),"Refusing downgrade")
    else:require(not receipt.exists(),"Receipt without framework DLLs")
    plugins=target(root,"BepInEx/plugins")
    if plugins.exists():
        known=set(inv0.values())|set(existing.values())|set(inv.values())
        for folder,dirs,files in os.walk(plugins,followlinks=False):
            for n in dirs+files:
                p=Path(folder)/n
                require(not p.is_symlink() and not getattr(p,"is_junction",lambda:False)(),"Linked plugin path refused")
            for n in files:
                p=Path(folder)/n
                if p.suffix.lower()!=".dll" or p in [plugins/n for n in DLLS]:continue
                require(n.casefold() not in {x.casefold() for x in DLLS} and hashfile(p) not in known,"Duplicate framework DLL")
    for n,(b,f) in base.items():
        if n.startswith(PLUGINS):continue
        p=target(root,n)
        if p.exists():
            if f["mode"]=="seed":continue
            require(hashfile(p)==sha(b),"Existing owned file differs: "+n)
        else:add(n,b)
    for n in sorted(DLLS):
        b=new.get(n)
        if b is None:b=target(root,PLUGINS+n).read_bytes() if n in existing else base[PLUGINS+n][0]
        require(sha(b)==inv[n],"Update needs an intermediate version; no files changed")
        add(PLUGINS+n,b)
    # Keep exact signed receipt; no locally fabricated release metadata.
    add(RECEIPT,envelope)
    transaction(root,plan,expected)
    require(all(hashfile(target(root,PLUGINS+n))==h for n,h in inv.items()),"Final inventory mismatch")
    print("PNC Framework",e.get("id"),"installed. Settings retained.")

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument("game_folder",nargs="?",help="Directory containing Post Nut Calamity.exe")
    args=p.parse_args()
    try:
        require(shutil.which("openssl"),"OpenSSL is required (normally present on Linux).")
        root=Path(args.game_folder or (str(Path.cwd()) if (Path.cwd()/"Post Nut Calamity.exe").is_file() else input("Game folder: ").strip().strip('"'))).expanduser().resolve(strict=True)
        validate_game(root)
        lock=target(root,".pnc-linux-install.lock")
        fd=os.open(lock,os.O_CREAT|os.O_EXCL|os.O_WRONLY,0o600);os.close(fd)
        try:
            b=download(BASE_URL);files=baseline(b)
            env=download(FEED,256*1024);e,_=release(env,files[UPDATER+"Trust.xml"][0])
            install(root,b,download(LOADER_URL),env,download(package_url(e)))
        finally:lock.unlink()
    except (ValueError,OSError,KeyError,TypeError,AttributeError,ET.ParseError,zipfile.BadZipFile) as ex:
        print("Installation stopped:",ex,file=sys.stderr);return 1
    return 0
if __name__=="__main__":sys.exit(main())
