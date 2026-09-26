#!/usr/bin/env python3
import os
import hashlib
import json
from pathlib import Path

VPNROUTER_ROOT = Path("/var/lib/dsh/Project/VPNRouter").resolve()
OMARCHY_ROOT = Path("/var/lib/dsh/Project/omarchy-vpnrouter").resolve()

EXCLUDED_DIRS = {
    ".git", ".vs", "bin", "obj", "TestResults", "node_modules",
    ".idea", "target", "build", ".dsh", ".turbo", ".gradle",
    "VPNRouter-cleanup-archives", "sources_wave3_archive"
}

BINARY_EXTENSIONS = {
    ".png", ".ico", ".icns", ".ttf", ".woff", ".woff2", ".eot",
    ".zip", ".tar", ".gz", ".aar", ".so", ".dylib", ".dll", ".exe",
    ".bin", ".dat", ".pdf", ".mp3", ".wav", ".ogg"
}

def is_binary(full_path):
    if full_path.suffix.lower() in BINARY_EXTENSIONS:
        return True
    try:
        with open(full_path, "rb") as f:
            chunk = f.read(1024)
            if b"\x00" in chunk:
                return True
    except:
        return True
    return False

def get_file_info(repo_id, rel_path, full_path):
    if is_binary(full_path):
        return None
        
    try:
        content = full_path.read_bytes()
    except Exception as e:
        return None
    
    sha256 = hashlib.sha256(content).hexdigest()
    byte_count = len(content)
    try:
        lines = content.decode('utf-8', errors='replace').splitlines()
        line_count = len(lines)
    except:
        line_count = 0
        
    ext = full_path.suffix.lower()
    name = full_path.name
    
    lang_map = {
        ".cs": "csharp",
        ".axaml": "axaml",
        ".java": "java",
        ".go": "go",
        ".qml": "qml",
        ".js": "javascript",
        ".py": "python",
        ".sh": "bash",
        ".ps1": "powershell",
        ".cmd": "batch",
        ".bat": "batch",
        ".json": "json",
        ".yaml": "yaml",
        ".yml": "yaml",
        ".md": "markdown",
        ".xml": "xml",
        ".props": "xml",
        ".csproj": "xml"
    }
    lang = lang_map.get(ext, "text")
    if name == "PKGBUILD":
        lang = "bash"

    p_str = str(rel_path).replace("\\", "/")
    
    # Exclude historical archived plans / release notes from active code map if needed,
    # but keep them categorized as documentation/history
    if p_str.startswith("VPNRouter.Tests/") or p_str.startswith("VPNRouter.Headless.Tests/"):
        classification = "first_party_test"
    elif p_str.startswith("packaging/") or "package" in p_str.lower() or ext in [".ps1", ".sh", ".cmd"] or name == "PKGBUILD":
        classification = "build_packaging_tooling"
    elif ext in [".json", ".yaml", ".yml", ".xml", ".props"] and not p_str.startswith("VPNRouter.Core/Models/"):
        classification = "config_catalog_schema"
    elif ext == ".md":
        classification = "documentation"
    elif ext in [".cs", ".java", ".go", ".qml", ".js"]:
        classification = "first_party_production"
    else:
        classification = "other"

    return {
        "repo_id": repo_id,
        "path": p_str,
        "full_path": str(full_path),
        "content_sha256": sha256,
        "byte_count": byte_count,
        "line_count": line_count,
        "language": lang,
        "classification": classification
    }

def scan_repo(repo_id, root):
    files = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in EXCLUDED_DIRS and not d.startswith(".git")]
        
        for f in filenames:
            if f.endswith(".tmp") or f.endswith(".swp") or f.endswith("~"):
                continue
            full_path = Path(dirpath) / f
            try:
                rel_path = full_path.relative_to(root)
            except ValueError:
                continue
            
            info = get_file_info(repo_id, rel_path, full_path)
            if info:
                files.append(info)
    return files

def assign_module(file_entry):
    repo = file_entry["repo_id"]
    p = file_entry["path"]
    
    if repo == "omarchy-vpnrouter":
        if p.startswith("ui/"): return "O03" # UI feature views
        elif p.startswith("lib/"): return "O04" # Shared JS/models
        elif p.startswith("bin/"): return "O05" # Launcher
        elif p.startswith("tests/"): return "O07" if "packag" in p else "O08" # Tests
        elif p == "setup": return "O06" # Setup
        elif p == "Service.qml": return "O02" # Service transport
        else: return "O01" # Manifest / Bar
            
    # VPNRouter.Core
    if p.startswith("VPNRouter.Core/"):
        sub = p[len("VPNRouter.Core/"):]
        if sub.startswith("Interfaces/"): return "C01"
        if sub.startswith("Models/"): return "C02"
        if sub.startswith("Json/") or sub.startswith("Yaml/"): return "C03"
        if sub.startswith("AppPaths.cs") or sub.startswith("AppVersion.cs"): return "C04"
        if sub.startswith("Services/VpnEngine") or sub.startswith("Services/StartupPipeline"): return "C05"
        if sub.startswith("Services/SingBoxManager"): return "C07"
        if sub.startswith("Services/TunOwnership") or sub.startswith("Services/ProcessOwnership") or sub.startswith("Services/LinuxTunOwnership") or sub.startswith("Services/UnixOwnedProcess"): return "C09"
        if sub.startswith("Services/ConfigGenerator") or sub.startswith("Services/ConfigPipeline") or sub.startswith("Services/ConfigSanity"): return "C12"
        if sub.startswith("Services/Firewall") or sub.startswith("Services/LeakProtection"): return "C16"
        if sub.startswith("Services/SplitTunnel") or sub.startswith("Services/EtwProcess"): return "C19"
        if sub.startswith("Services/ProfileManager") or sub.startswith("Services/ProfileApplication") or sub.startswith("Services/Settings"): return "C21"
        if sub.startswith("Services/FreeConfigs") or sub.startswith("Services/Subscription") or sub.startswith("Services/ServerUri"): return "C24"
        if sub.startswith("Services/Health") or sub.startswith("Services/ServerHealth") or sub.startswith("Services/AutoFailover") or sub.startswith("Services/VlessDeepVerifier"): return "C27"
        if sub.startswith("Services/Zapret") or sub.startswith("Services/TgProxy") or sub.startswith("Services/Slipstream"): return "C30"
        if sub.startswith("Platform/Linux") or sub.startswith("Services/Linux"): return "C33"
        if sub.startswith("Platform/Mac") or sub.startswith("Services/Mac"): return "C34"
        if sub.startswith("Platform/Windows") or sub.startswith("Services/Windows"): return "C35"
        if sub.startswith("Services/Update") or sub.startswith("Services/Diagnostics"): return "C37"
        return "C39" # Residual Core
        
    if p.startswith("VPNRouter.App/"):
        sub = p[len("VPNRouter.App/"):]
        if sub.startswith("ViewModels/"): return "A02"
        if sub.startswith("Views/"): return "A06"
        if sub.startswith("Controls/") or sub.startswith("Styles/"): return "A07"
        if sub.startswith("Services/"): return "A01"
        return "A05" # Shell / nav / app
        
    if p.startswith("VPNRouter.Headless/"):
        sub = p[len("VPNRouter.Headless/"):]
        if sub.startswith("Protocol/"): return "H02"
        if sub.startswith("Features/"): return "H07"
        if sub.startswith("Storage/"): return "H08"
        if sub.startswith("Lifecycle/"): return "H06"
        if sub.startswith("RouterBackend.cs") or sub.startswith("RouterSession.cs"): return "H05"
        return "H01"
        
    if p.startswith("VPNRouter.Android/"):
        sub = p[len("VPNRouter.Android/"):]
        if "VpnService" in sub or "Lib" in sub or sub.endswith(".java"): return "D02"
        if "PerApp" in sub: return "D05"
        if "FreeConfig" in sub or "Server" in sub: return "D06"
        if "Update" in sub or "Diag" in sub: return "D08"
        if sub.endswith(".axaml") or "Controls" in sub: return "D09"
        return "D01"
        
    if p.startswith("VPNRouter.Service/"): return "S01"
    if p.startswith("VPNRouter.CLI/"): return "L01"
    if p.startswith("VPNRouter.GUI/"): return "G01"
    if p.startswith("VPNRouter.Headless.Tests/"): return "T05"
    if p.startswith("VPNRouter.Tests/"): return "T01"
    if p.startswith("packaging/") or p.startswith("tools/"): return "R03"
    if p.startswith("profiles/") or p.startswith("samples/"): return "R05"
    if p.startswith("plans/") or p.startswith("docs/"): return "R06"
    return "R01"

def partition_batches(files, target_lines=2500, max_lines=4000):
    modules = {}
    for f in files:
        m = assign_module(f)
        f["module_id"] = m
        modules.setdefault(m, []).append(f)
        
    batches = {}
    batch_idx = 1
    
    for mod_id in sorted(modules.keys()):
        mod_files = sorted(modules[mod_id], key=lambda x: x["path"])
        cur_batch = []
        cur_lines = 0
        
        for f in mod_files:
            flines = f["line_count"]
            if cur_lines + flines > max_lines and cur_batch:
                b_id = f"B{batch_idx:03d}_{mod_id}"
                batches[b_id] = {
                    "batch_id": b_id,
                    "module_id": mod_id,
                    "files": cur_batch,
                    "total_lines": cur_lines,
                    "status": "pending"
                }
                batch_idx += 1
                cur_batch = []
                cur_lines = 0
                
            cur_batch.append(f)
            cur_lines += flines
            
            if cur_lines >= target_lines:
                b_id = f"B{batch_idx:03d}_{mod_id}"
                batches[b_id] = {
                    "batch_id": b_id,
                    "module_id": mod_id,
                    "files": cur_batch,
                    "total_lines": cur_lines,
                    "status": "pending"
                }
                batch_idx += 1
                cur_batch = []
                cur_lines = 0
                
        if cur_batch:
            b_id = f"B{batch_idx:03d}_{mod_id}"
            batches[b_id] = {
                "batch_id": b_id,
                "module_id": mod_id,
                "files": cur_batch,
                "total_lines": cur_lines,
                "status": "pending"
            }
            batch_idx += 1
            
    return modules, batches

def main():
    print("Scanning text files in VPNRouter...")
    vpn_files = scan_repo("vpnrouter", VPNROUTER_ROOT)
    print(f"VPNRouter text files: {len(vpn_files)}")
    
    omarchy_files = []
    if OMARCHY_ROOT.exists():
        print("Scanning text files in omarchy-vpnrouter...")
        omarchy_files = scan_repo("omarchy-vpnrouter", OMARCHY_ROOT)
        print(f"omarchy-vpnrouter text files: {len(omarchy_files)}")
        
    all_files = vpn_files + omarchy_files
    modules, batches = partition_batches(all_files)
    
    total_loc = sum(f["line_count"] for f in all_files)
    prod_loc = sum(f["line_count"] for f in all_files if f["classification"] == "first_party_production")
    test_loc = sum(f["line_count"] for f in all_files if f["classification"] == "first_party_test")
    
    print(f"Total files: {len(all_files)}, Total lines: {total_loc} (Prod: {prod_loc}, Test: {test_loc})")
    print(f"Total modules: {len(modules)}, Total batches: {len(batches)}")
    
    out_dir = Path("/var/lib/dsh/Project/VPNRouter/plans/agent-map/inventory")
    out_dir.mkdir(parents=True, exist_ok=True)
    
    manifest_data = {
        "total_files": len(all_files),
        "total_lines": total_loc,
        "production_lines": prod_loc,
        "test_lines": test_loc,
        "modules_count": len(modules),
        "batches_count": len(batches),
        "files": all_files
    }
    
    with open(out_dir / "manifest.json", "w", encoding="utf-8") as f:
        json.dump(manifest_data, f, indent=2, ensure_ascii=False)
        
    with open(out_dir / "batches.json", "w", encoding="utf-8") as f:
        json.dump(batches, f, indent=2, ensure_ascii=False)
        
    print("Manifest and Batches generated successfully.")

if __name__ == "__main__":
    main()
