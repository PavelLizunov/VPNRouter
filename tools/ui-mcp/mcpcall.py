#!/usr/bin/env python3
"""Calls tools of the VPNRouter UI MCP server from a shell: starts the server, runs the calls, saves images, prints text.

usage: mcpcall.py [--out DIR] --server CMD [ARG ...] -- TOOL 'JSON-ARGS' [TOOL 'JSON-ARGS' ...]

Examples:
  mcpcall.py --out /tmp/ui --server ./vpnrouter-ui-mcp -- ui_render '{"surface":"apps","width":520,"height":1100}'
  mcpcall.py --out /tmp/ui --server ssh tester@host "dotnet C:\\\\path\\\\vpnrouter-ui-mcp.dll" -- ui_catalog '{}'
"""
import argparse
import base64
import json
import os
import subprocess
import sys


def main():
    argv = sys.argv[1:]
    if "--" not in argv:
        sys.exit(__doc__)
    split = argv.index("--")
    head, calls = argv[:split], argv[split + 1:]
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--out", default=".")
    parser.add_argument("--server", nargs=argparse.REMAINDER, required=True)
    opts = parser.parse_args(head)
    if not calls or len(calls) % 2:
        sys.exit("tool calls come in pairs: TOOL 'JSON-ARGS'")
    os.makedirs(opts.out, exist_ok=True)

    server = subprocess.Popen(opts.server, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, bufsize=1)

    def rpc(ident, method, params=None):
        server.stdin.write(json.dumps({"jsonrpc": "2.0", "id": ident, "method": method, "params": params or {}}) + "\n")
        server.stdin.flush()
        while True:
            line = server.stdout.readline()
            if not line:
                sys.exit("server closed the pipe: " + server.stderr.read()[:2000])
            try:
                message = json.loads(line)
            except ValueError:
                continue
            if message.get("id") == ident:
                return message

    rpc(1, "initialize", {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "mcpcall", "version": "0"}})
    server.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
    server.stdin.flush()

    failed = False
    images = 0
    for index in range(0, len(calls), 2):
        tool, arguments = calls[index], json.loads(calls[index + 1])
        reply = rpc(10 + index, "tools/call", {"name": tool, "arguments": arguments})
        if "error" in reply:
            print("RPC ERROR", reply["error"])
            failed = True
            continue
        result = reply["result"]
        failed = failed or bool(result.get("isError"))
        print(f"=== {tool} {json.dumps(arguments)[:140]} isError={result.get('isError')}")
        for item in result["content"]:
            if item["type"] == "text":
                print(item["text"])
            elif item["type"] == "image":
                images += 1
                path = os.path.join(opts.out, f"{images:02d}-{tool}.png")
                with open(path, "wb") as handle:
                    handle.write(base64.b64decode(item["data"]))
                print("IMAGE", path)
    server.stdin.close()
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
