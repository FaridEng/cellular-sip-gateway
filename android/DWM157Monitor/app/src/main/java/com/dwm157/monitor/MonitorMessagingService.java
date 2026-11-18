package com.dwm157.monitor;

import com.google.firebase.messaging.FirebaseMessagingService;
import com.google.firebase.messaging.RemoteMessage;
import java.util.Map;

public final class MonitorMessagingService extends FirebaseMessagingService {
    @Override public void onNewToken(String token) { PushRegistration.register(this, token); }

    @Override public void onMessageReceived(RemoteMessage message) {
        if (!getSharedPreferences("monitor", MODE_PRIVATE).getBoolean("push_enabled", false)) return;
        Map<String, String> data = message.getData();
        try {
            long sequence = Long.parseLong(data.get("sequence"));
            long sentAt = java.time.Instant.parse(data.get("timestampUtc")).toEpochMilli();
            long age = System.currentTimeMillis() - sentAt;
            if (age > 10 * 60_000L || age < -120_000L) return;
            String source = data.get("observer");
            if (source == null || !data.containsKey("eventId")) return;
            String sourceId = data.containsKey("observerId") ? data.get("observerId") : source;
            String category = "test".equals(data.get("kind")) ? "observer-test:" : "observer:";
            AlertNotifications.show(this, category + sourceId, sequence,
                    data.get("title"), source + ": " + data.get("message"));
        } catch (Exception ignored) { /* No token or payload logging. */ }
    }
}
