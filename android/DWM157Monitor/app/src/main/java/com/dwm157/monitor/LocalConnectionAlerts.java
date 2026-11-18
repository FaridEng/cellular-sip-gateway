package com.dwm157.monitor;

import android.content.Context;
import android.content.SharedPreferences;

/** S25 foreground observations only. They never imply that the NUC sees the same path. */
final class LocalConnectionAlerts {
    static synchronized void observe(Context context, boolean healthy, String title, String detail) {
        SharedPreferences prefs = context.getSharedPreferences("local-connection-alert", Context.MODE_PRIVATE);
        long now = System.currentTimeMillis();
        long first = prefs.getLong("first_failure", 0);
        int failures = prefs.getInt("failures", 0), successes = prefs.getInt("successes", 0);
        boolean incident = prefs.getBoolean("incident", false);
        SharedPreferences.Editor edit = prefs.edit();
        if (healthy) {
            successes++;
            edit.putInt("failures", 0).putInt("successes", successes);
            if (!incident) edit.putLong("first_failure", 0);
            if (incident && successes >= 2) {
                AlertNotifications.show(context, "s25-direct", now, "S25 Gateway connection restored", "The direct S25-to-Gateway PC request is working again.");
                edit.putBoolean("incident", false).putLong("first_failure", 0);
            }
        } else {
            if (first == 0 || now < first || now - first > 24 * 3600_000L) { first = now; failures = 0; }
            failures++;
            edit.putLong("first_failure", first).putInt("failures", failures).putInt("successes", 0);
            if (!incident && failures >= 3 && now - first >= 45_000L) {
                AlertNotifications.show(context, "s25-direct", now, title, detail);
                edit.putBoolean("incident", true);
            }
        }
        edit.apply();
    }
}
