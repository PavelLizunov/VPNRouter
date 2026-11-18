package com.ninitux.vpnrouter;

import java.io.File;
import java.nio.file.Files;
import java.nio.file.Path;

public final class NativeReportPrivacyTest {
    public static void main(String[] args) throws Exception {
        File root = Path.of(args[0]).toFile().getCanonicalFile();
        File working = new File(root, "data");
        Path report = working.toPath().resolve("crash_reports").resolve("sample");
        Files.createDirectories(report);
        Path preferences = root.toPath().resolve("preferences.fixture");
        File noBackup = Files.createDirectory(root.toPath().resolve("no-backup-fixture")).toFile();
        String fixture = "synthetic fixture";
        Files.writeString(preferences, fixture);
        Path snapshot = root.toPath().resolve("configuration.json");
        Files.writeString(snapshot, fixture);
        Files.writeString(report.resolve("configuration.json"), fixture);
        Files.writeString(report.resolve("metadata.json"), fixture);
        Path outside = Files.createDirectory(root.toPath().resolve("unrelated"));
        Files.writeString(outside.resolve("configuration.json"), fixture);
        Files.createSymbolicLink(report.getParent().resolve("unrelated"), outside);
        String crash = "CrashReport-vpnrouter.log";
        Files.writeString(working.toPath().resolve(crash + ".old"), fixture);

        for (int pass = 0; pass < 2; pass++) {
            NativeReportPrivacy.prepare(root, working, noBackup, crash,
                    (target, link) -> Files.createSymbolicLink(Path.of(link), Path.of(target)));
            for (Path sink : new Path[] {snapshot, working.toPath().resolve(crash), working.toPath().resolve(crash + ".old")}) {
                require(Files.readSymbolicLink(sink).equals(Path.of("/dev/null")));
                Files.writeString(sink, fixture);
                require(Files.readAllBytes(sink).length == 0);
            }
        }
        require(!Files.exists(report.resolve("configuration.json")));
        require(Files.readString(report.resolve("metadata.json")).equals(fixture));
        File[] versions = new File(noBackup, "libbox-legacy-recovery").listFiles();
        require(versions != null && versions.length == 1);
        for (String relative : new String[] {"configuration.json", "data/crash_reports/sample/configuration.json",
                "data/" + crash + ".old"})
            require(Files.readString(versions[0].toPath().resolve(relative)).equals(fixture));
        require(Files.readString(preferences).equals(fixture));
        require(Files.readString(outside.resolve("configuration.json")).equals(fixture));
    }

    private static void require(boolean value) {
        if (!value) throw new AssertionError("Native report privacy regression");
    }
}
