package com.ninitux.vpnrouter;

import java.io.File;
import java.io.IOException;
import java.util.UUID;

/** Layout of the report-only files written by the pinned native runtime. */
final class NativeReportPrivacy {
    interface Symlink {
        void create(String target, String link) throws Exception;
    }

    private NativeReportPrivacy() { }

    static void prepare(File base, File working, File noBackup, String crashName, Symlink symlink) throws Exception {
        File archive = new File(noBackup, "libbox-legacy-recovery/" + System.currentTimeMillis() + "-" + UUID.randomUUID());
        for (File old : new File[] {new File(base, "configuration.json"), new File(working, crashName),
                new File(working, crashName + ".old")}) {
            archiveIfRegular(base, archive, old);
        }
        discard(new File(base, "configuration.json"), symlink);
        discard(new File(working, crashName), symlink);
        discard(new File(working, crashName + ".old"), symlink);

        // A previous version may already have copied a full config into older reports.
        for (String name : new String[] {"crash_reports", "oom_reports", "power_reports"}) {
            File reports = new File(working, name);
            if (!realDirectory(reports)) continue;
            File[] children = reports.listFiles();
            if (children == null) throw new IOException("Native report directory unreadable");
            for (File report : children) {
                if (realDirectory(report)) {
                    archiveIfRegular(base, archive, new File(report, "configuration.json"));
                    if (name.equals("crash_reports"))
                        archiveIfRegular(base, archive, new File(report, "go.log"));
                }
            }
        }
        for (String name : new String[] {"oom_draft", "power_draft"}) {
            File draft = new File(working, name);
            if (realDirectory(draft)) archiveIfRegular(base, archive, new File(draft, "configuration.json"));
        }
    }

    private static void discard(File destination, Symlink symlink) throws Exception {
        File link = new File(destination.getParentFile(), ".native-discard-" + UUID.randomUUID());
        symlink.create("/dev/null", link.getPath());
        // Both names are in one app-private directory. Android rename replaces the old entry atomically.
        if (!link.renameTo(destination)) {
            link.delete();
            throw new IOException("Native diagnostic sink unavailable");
        }
    }

    private static boolean realDirectory(File entry) throws IOException {
        return entry.isDirectory() && entry.equals(entry.getCanonicalFile());
    }

    private static void archiveIfRegular(File base, File archive, File original) throws IOException {
        if (!original.isFile() || !original.equals(original.getCanonicalFile())) return;
        String relative = original.getPath().substring(base.getPath().length() + 1);
        File saved = new File(archive, relative);
        File parent = saved.getParentFile();
        if (!parent.equals(parent.getCanonicalFile()) || (!parent.isDirectory() && !parent.mkdirs()) ||
                saved.exists() || !original.renameTo(saved))
            throw new IOException("Unable to retain previous native diagnostic");
    }
}
