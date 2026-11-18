package com.dwm157.monitor;

import android.content.Context;
import android.content.SharedPreferences;
import org.json.JSONObject;

final class PushRegistration {
    static void register(Context context, String fcmToken) {
        Context app = context.getApplicationContext();
        SharedPreferences prefs = app.getSharedPreferences("monitor", Context.MODE_PRIVATE);
        if (!prefs.getBoolean("push_enabled", false)) return;
        try {
            new TokenStore(app, "fcm-").save(fcmToken);
            androidx.work.Constraints constraints = new androidx.work.Constraints.Builder()
                    .setRequiredNetworkType(androidx.work.NetworkType.CONNECTED).build();
            androidx.work.OneTimeWorkRequest work = new androidx.work.OneTimeWorkRequest.Builder(PushRegistrationWorker.class)
                    .setConstraints(constraints).setBackoffCriteria(androidx.work.BackoffPolicy.EXPONENTIAL,
                            30, java.util.concurrent.TimeUnit.SECONDS).build();
            androidx.work.WorkManager.getInstance(app).enqueueUniqueWork("register-observer-push", androidx.work.ExistingWorkPolicy.REPLACE, work);
            prefs.edit().putString("push_registration_status", "Registration queued; waiting for observer connectivity").apply();
        } catch (Exception ignored) {
            prefs.edit().putString("push_registration_status", "Registration could not be queued securely").apply();
        }
    }

    static void reportVersion(Context context, ApiClient api, String endpoint, String token) {
        SharedPreferences prefs = context.getSharedPreferences("monitor", Context.MODE_PRIVATE);
        long now = System.currentTimeMillis();
        if (now < prefs.getLong("next_version_report_attempt", 0)) return;
        try {
            android.content.pm.PackageInfo info = context.getPackageManager().getPackageInfo("com.tailscale.ipn", 0);
            boolean changed = !info.versionName.equals(prefs.getString("reported_tailscale_version", ""));
            if (!changed && now - prefs.getLong("version_reported_at", 0) < 3600_000L) return;
            prefs.edit().putLong("next_version_report_attempt", now + 60_000L).apply();
            api.post(endpoint, token, "/api/v1/versions/report", new JSONObject()
                    .put("name", android.os.Build.MODEL).put("installedVersion", info.versionName));
            prefs.edit().putString("reported_tailscale_version", info.versionName).putLong("version_reported_at", now).apply();
        } catch (Exception ignored) { /* Missing package/failed inventory must not affect alerts. */ }
    }
}
