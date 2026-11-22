"""Pack DB-IP Lite CSV ranges into the desktop app's offline lookup tables."""

import argparse
import csv
import gzip
import ipaddress
from pathlib import Path
from contextlib import ExitStack


def pack(source: Path, output: Path) -> None:
    output.mkdir(parents=True, exist_ok=True)
    last = {4: -1, 6: -1}
    with ExitStack() as stack:
        data = stack.enter_context(gzip.open(source, "rt", encoding="utf-8", newline=""))
        tables = {
            version: stack.enter_context(
                gzip.GzipFile(filename="", mode="wb", mtime=0, fileobj=stack.enter_context(
                    (output / f"ipv{version}.ranges.gz").open("wb")
                ))
            )
            for version in last
        }
        for first, final, code in csv.reader(data):
            lo, hi = ipaddress.ip_address(first), ipaddress.ip_address(final)
            if lo.version != hi.version or not last[lo.version] < int(lo) <= int(hi):
                raise ValueError("Country ranges must be sorted and disjoint")
            if len(code) != 2 or any(not "A" <= letter <= "Z" for letter in code):
                raise ValueError("Invalid ASCII country code")
            last[lo.version] = int(hi)
            if code != "ZZ":
                tables[lo.version].write(lo.packed + hi.packed + code.encode("ascii"))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    pack(args.source, args.output)
