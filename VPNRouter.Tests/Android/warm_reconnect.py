"""Android 14 device fixture for #689. Use the offline acceptance device and its loopback
SOCKS / reject-TUN profile. Connect once through Home and leave it open with consent granted.
Run for each saved Off/Include/Exclude selection, supplying Home button centres in pixels:
python warm_reconnect.py SERIAL --stop X Y --connect X Y
"""

import argparse
import re
import subprocess
import time


PACKAGE = "com.ninitux.vpnrouter"


def active_vpn(dump, uid):
    # Other sections retain historical networks after Stop.
    _, start, rest = dump.partition("Current Networks:")
    networks, end, _ = rest.partition("Status for known UIDs:")
    if not start or not end:
        raise RuntimeError("Unrecognized Android connectivity dump")
    for line in networks.splitlines():
        if not line.lstrip().startswith("NetworkAgentInfo{") or "Transports: VPN" not in line:
            continue
        if not re.search(rf"\bOwnerUid: {uid}\b", line):
            raise RuntimeError("A different app owns the VPN")
        network = re.search(r"network\{(\d+)\}", line)
        ranges = re.search(r"\bUids?: (?:<([^>]+)>|(\d+))", line)
        if not network or not ranges:
            raise RuntimeError("Missing network ID or Android VPN UID ranges")
        uids = frozenset(re.findall(r"\d+(?:-\d+)?", ranges[1] or ranges[2]))
        if not uids:
            raise RuntimeError("Empty Android VPN UID ranges")
        return network[1], uids
    return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("serial")
    for button in ("stop", "connect"):
        parser.add_argument("--" + button, type=int, nargs=2, required=True, metavar=("X", "Y"))
    args = parser.parse_args()

    def shell(*command):
        return subprocess.check_output(["adb", "-s", args.serial, "shell", *command],
                                       text=True, timeout=10).strip()

    packages = shell("pm", "list", "packages", "-U", "--user", "0", PACKAGE)
    uid = re.search(rf"^package:{re.escape(PACKAGE)} uid:(\d+)$", packages, re.MULTILINE)
    if not uid:
        raise RuntimeError("Fixture package missing for user 0")
    pid = shell("pidof", PACKAGE)
    if not pid.isdecimal():
        raise RuntimeError("Expected one running VPNRouter process")

    def state():
        if shell("pidof", PACKAGE) != pid:
            raise RuntimeError("VPNRouter process changed during warm reconnect")
        return active_vpn(shell("dumpsys", "connectivity"), uid[1])

    def tap_and_wait(point, connected):
        if (shell("settings", "get", "global", "airplane_mode_on") != "1"
                or "Wifi is disabled" not in shell("cmd", "wifi", "status").splitlines()):
            raise RuntimeError("Fixture requires airplane mode with Wi-Fi off")
        focus = shell("dumpsys", "window", "displays")
        if not re.search(r"mCurrentFocus=.*\b" + re.escape(PACKAGE) + r"/", focus):
            raise RuntimeError("VPNRouter Home must be in the foreground")
        shell("input", "tap", *(str(n) for n in point))
        deadline = time.monotonic() + 45
        while time.monotonic() < deadline:
            current = state()
            if (current is not None) == connected:
                return current
            time.sleep(0.25)
        raise RuntimeError("Timed out waiting for Android VPN " + ("up" if connected else "down"))

    first = state()
    if first is None:
        raise RuntimeError("Start the loopback fixture using the Home Connect button first")
    tap_and_wait(args.stop, False)
    time.sleep(1)  # Allow Home to paint Connect after the OS removes the network.
    second = tap_and_wait(args.connect, True)
    time.sleep(1)
    settled = state()
    if settled is not None:
        tap_and_wait(args.stop, False)
    if settled != second or second[0] == first[0] or second[1] != first[1]:
        raise RuntimeError("Warm Connect did not restore a new stable VPN with the same UID ranges")
    print(f"Warm reconnect passed in PID {pid}, Android networks {first[0]} -> {second[0]}")


if __name__ == "__main__":
    main()
