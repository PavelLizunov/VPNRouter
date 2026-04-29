// VpnRouterService — Android VpnService wrapper that integrates libbox.aar.
//
// v3.0 Android Phase 1 SKELETON (2026-04-29).
// This file is a TODO scaffold — does NOT yet build because:
//   1. libbox.aar isn't checked in / present (requires gradle build from
//      sagernet/sing-box-for-android matching our desktop sing-box version,
//      currently 1.13.10).
//   2. Java/Kotlin classpath isn't wired in VPNRouter.Android.csproj yet
//      (would need <AndroidLibrary Include="Lib/libbox.aar" /> ItemGroup
//      and Mono.Android Java callable wrapper generation).
//
// Reference impl: sagernet/sing-box-for-android/app/src/main/java/io/nekohasekai/sfa/bg/BoxService.kt
//
// Phase 1 next steps (see plans/vpnrouter-android-phase1-roadmap.md):
//   1. Build libbox.aar locally:
//        git clone https://github.com/SagerNet/sing-box-for-android
//        cd sing-box-for-android && git checkout v1.13.10  # or current
//        ./gradlew :libbox:bundleLibboxAar
//      Drop the resulting .aar at VPNRouter.Android/Lib/libbox.aar.
//   2. Uncomment the io.nekohasekai.libbox imports below.
//   3. Add <AndroidLibrary Include="Lib/libbox.aar" Bind="false" />
//      to VPNRouter.Android.csproj.
//   4. Wire AndroidSingBoxRuntime.cs (C# side) to start/stop this service
//      via Intent.
//   5. Implement PlatformInterface (libbox callbacks → Kotlin → C# bridge).
//
// For now this file only exists to (a) reserve the class layout, (b)
// document the Phase 1 architecture for the next session, (c) make the
// reverse-DNS package allowed-list integration explicit.
//
// THIS FILE IS NOT INCLUDED IN THE CURRENT BUILD — it sits in the project
// source tree as a forward-declaration placeholder.
package com.ninitux.vpnrouter

// import io.nekohasekai.libbox.Libbox
// import io.nekohasekai.libbox.PlatformInterface
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Intent
import android.net.VpnService
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat

class VpnRouterService : VpnService() {

    companion object {
        const val ACTION_START = "com.ninitux.vpnrouter.START"
        const val ACTION_STOP = "com.ninitux.vpnrouter.STOP"
        const val EXTRA_CONFIG_JSON = "config_json"
        const val EXTRA_ALLOWED_PACKAGES = "allowed_packages"  // Array<String>

        const val NOTIFICATION_ID = 100
        const val NOTIFICATION_CHANNEL_ID = "vpnrouter_tunnel"
    }

    // private var libbox: Libbox.Service? = null
    private var pendingConfigJson: String? = null
    private var pendingAllowedPackages: Array<String>? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val action = intent?.action
        when (action) {
            ACTION_START -> {
                pendingConfigJson = intent.getStringExtra(EXTRA_CONFIG_JSON)
                pendingAllowedPackages = intent.getStringArrayExtra(EXTRA_ALLOWED_PACKAGES)
                startTunnel()
            }
            ACTION_STOP -> {
                stopTunnel()
                stopSelf()
            }
            else -> { /* ignored */ }
        }
        return START_NOT_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun startTunnel() {
        // Foreground notification (mandatory for VpnService API 26+).
        startForeground(NOTIFICATION_ID, buildNotification())

        // Build VpnService configuration.
        val builder = Builder()
            .setSession("VPNRouter")
            .setMtu(1500)
            // sing-box uses 172.19.0.1/30 for its internal TUN by default;
            // mirror that here so libbox's TunService finds the right
            // interface on FD handoff.
            .addAddress("172.19.0.1", 30)
            .addAddress("fdfe:dcba:9876::1", 126)  // IPv6 leak protection
            .addRoute("0.0.0.0", 0)
            .addRoute("::", 0)
            // Default DNS — libbox can override per-config.
            .addDnsServer("1.1.1.1")
            .addDnsServer("1.0.0.1")

        // Per-app routing — only route the listed packages through TUN.
        // Empty list = full-tunnel (everything routes).
        // From Profile.AndroidPackages (parsed from default-android.json
        // or user customisation in Settings → Applications).
        pendingAllowedPackages?.forEach { pkg ->
            try {
                builder.addAllowedApplication(pkg)
            } catch (e: Exception) {
                // PackageManager.NameNotFoundException — package not installed.
                // Log + skip; full-tunnel fallback would be wrong (we'd
                // include packages the user DID select).
                android.util.Log.w("VpnRouter", "Package not found: $pkg")
            }
        }

        // CRITICAL — exclude OUR OWN package so we don't loop traffic back
        // through ourselves. Otherwise the OS would route our own DNS
        // queries (to our SOCKS proxy) back through the TUN → infinite
        // loop → tunnel collapse.
        try {
            builder.addDisallowedApplication(packageName)
        } catch (e: Exception) {
            android.util.Log.e("VpnRouter", "FATAL: cannot exclude self: ${e.message}")
        }

        val pfd = builder.establish()
        if (pfd == null) {
            android.util.Log.e("VpnRouter", "VpnService.Builder.establish returned null — user denied or system rejected")
            stopSelf()
            return
        }

        // TODO Phase 1 step 5: hand the FD to libbox.
        //   val tunFd = pfd.fd
        //   val service = Libbox.newService(pendingConfigJson, MyPlatformInterface(this))
        //   service.start(tunFd)
        //   libbox = service
        // For now, we just hold the FD and immediately tear down.
        try { pfd.close() } catch (_: Exception) {}
    }

    private fun stopTunnel() {
        // libbox?.close()
        // libbox = null
        stopForeground(STOP_FOREGROUND_REMOVE)
    }

    override fun onRevoke() {
        // System-initiated revoke (user toggled VPN off in Settings, or
        // another VPN app took over). libbox should clean up gracefully.
        stopTunnel()
        super.onRevoke()
    }

    override fun onDestroy() {
        stopTunnel()
        super.onDestroy()
    }

    private fun buildNotification(): Notification {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                NOTIFICATION_CHANNEL_ID,
                "VPNRouter Tunnel",
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "VPN tunnel running"
                setShowBadge(false)
            }
            getSystemService(NotificationManager::class.java)?.createNotificationChannel(channel)
        }

        val stopIntent = Intent(this, VpnRouterService::class.java).apply {
            action = ACTION_STOP
        }
        val stopPi = PendingIntent.getService(
            this, 0, stopIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        return NotificationCompat.Builder(this, NOTIFICATION_CHANNEL_ID)
            .setContentTitle("VPNRouter")
            .setContentText("Tunnel active")
            .setSmallIcon(android.R.drawable.ic_lock_idle_lock)  // TODO: real icon
            .setOngoing(true)
            .addAction(android.R.drawable.ic_menu_close_clear_cancel, "Disconnect", stopPi)
            .build()
    }
}
