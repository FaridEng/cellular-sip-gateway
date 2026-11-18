package com.dwm157.monitor;

import android.app.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.os.Build;

final class AlertNotifications {
    static final String CHANNEL = "gateway-health";

    static void ensureChannel(Context context) {
        NotificationManager manager = context.getSystemService(NotificationManager.class);
        NotificationChannel channel = new NotificationChannel(CHANNEL, "Gateway connection alerts", NotificationManager.IMPORTANCE_HIGH);
        channel.setDescription("Connection loss, Gateway health and recovery notifications");
        manager.createNotificationChannel(channel);
    }

    static boolean permitted(Context context) {
        return (Build.VERSION.SDK_INT < 33 || context.checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED)
                && context.getSystemService(NotificationManager.class).areNotificationsEnabled();
    }

    static void show(Context context, String source, long sequence, String title, String body) {
        ensureChannel(context);
        if (!permitted(context)) return;
        SharedPreferences saved = context.getSharedPreferences("alert-delivery", Context.MODE_PRIVATE);
        synchronized (AlertNotifications.class) {
            if (sequence <= saved.getLong(source, 0)) return;
            Intent intent = new Intent(context, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP);
            PendingIntent tap = PendingIntent.getActivity(context, 0, intent, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
            Notification notification = new Notification.Builder(context, CHANNEL)
                    .setSmallIcon(android.R.drawable.stat_notify_error)
                    .setContentTitle(title).setContentText(body)
                    .setStyle(new Notification.BigTextStyle().bigText(body))
                    .setContentIntent(tap).setAutoCancel(true).setShowWhen(true).build();
            context.getSystemService(NotificationManager.class).notify(source, 100, notification);
            saved.edit().putLong(source, sequence).apply();
        }
    }
}
