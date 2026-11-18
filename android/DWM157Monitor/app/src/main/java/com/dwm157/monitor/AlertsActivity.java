package com.dwm157.monitor;

import android.app.Activity;
import android.content.*;
import android.os.*;
import android.text.InputType;
import android.widget.*;
import com.google.firebase.FirebaseApp;
import com.google.firebase.messaging.FirebaseMessaging;

public final class AlertsActivity extends Activity {
    private EditText endpoint, token;
    private CheckBox enabled;
    private TextView status, feedback;
    private SharedPreferences prefs;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        prefs = getSharedPreferences("monitor", MODE_PRIVATE);
        LinearLayout root = MonitorUi.screen(this, "Alerts & Push", "Independent NUC observer · Pacific Time", true);
        status = MonitorUi.section(root, "Notification status");
        LinearLayout form = MonitorUi.card(root);
        MonitorUi.note(form, "Receive connection alerts while this app is closed. Keep NUC running and this phone online.");
        endpoint = MonitorUi.field(form, "Observer URL", false);
        endpoint.setHint("http://NUC-TAILSCALE-IP:5090");
        endpoint.setText(prefs.getString("observer_endpoint", ""));
        token = MonitorUi.field(form, "Independent observer token", true);
        token.setText(new TokenStore(this, "observer-").load());
        MonitorUi.note(form, "Use the NUC observer token here. The Gateway PC monitoring token is separate.");
        enabled = new CheckBox(this);
        enabled.setText("Enable observer Push notifications");
        enabled.setTextColor(MonitorUi.ink(this));
        enabled.setMinHeight(MonitorUi.dp(this, 48));
        enabled.setChecked(prefs.getBoolean("push_enabled", false));
        form.addView(enabled, MonitorUi.wrap());
        Button save = MonitorUi.button(this, "Save & register Push", true);
        save.setOnClickListener(view -> save());
        MonitorUi.row(form, save);
        Button test = MonitorUi.button(this, "Send test Push", false);
        test.setOnClickListener(view -> {
            test.setEnabled(false);
            feedback.setText("Sending test Push…");
            new Thread(() -> {
            try {
                new ApiClient().post(prefs.getString("observer_endpoint", ""), new TokenStore(this, "observer-").load(),
                        "/api/v1/push/test", new org.json.JSONObject());
                runOnUiThread(() -> feedback.setText("Test accepted · " + PacificTime.clock(java.time.Instant.now().toString())
                        + "\nCheck that the notification appears on this phone."));
            } catch (Exception ignored) {
                runOnUiThread(() -> feedback.setText("Test not accepted. Check registration and the NUC observer connection, then retry."));
            }
            finally { runOnUiThread(() -> test.setEnabled(true)); }
        }, "push-test").start();
        });
        MonitorUi.row(form, test);
        feedback = MonitorUi.note(form, "");
        Button settings = MonitorUi.button(this, "Android notification settings", false);
        settings.setOnClickListener(view -> startActivity(new Intent(android.provider.Settings.ACTION_APP_NOTIFICATION_SETTINGS)
                .putExtra(android.provider.Settings.EXTRA_APP_PACKAGE, getPackageName())));
        MonitorUi.row(root, settings);
        refreshStatus();
    }

    @Override public void onResume() { super.onResume(); if (status != null) refreshStatus(); }

    private TextView label(LinearLayout root, String text, int size) {
        TextView value = new TextView(this); value.setText(text); value.setTextSize(size);
        value.setTextColor(MonitorUi.ink(this)); value.setPadding(0,12,0,8);
        root.addView(value); return value;
    }

    private void refreshStatus() {
        long registeredAt = prefs.getLong("push_registered_at", 0);
        status.setText(MonitorUi.rows(this, "Firebase: " + (BuildConfig.FIREBASE_CONFIGURED ? "Configured" : "Not configured in this build")
                + "\nAndroid notifications: " + (AlertNotifications.permitted(this) ? "Allowed" : "Not allowed")
                + "\nRegistration: " + prefs.getString("push_registration_status", "Not registered")
                + (registeredAt > 0 ? "\nRegistered: " + PacificTime.format(java.time.Instant.ofEpochMilli(registeredAt).toString()) : "")
                + "\n\nDirect S25 checks run only while the overview is visible."));
    }

    private void save() {
        if (enabled.isChecked() && endpoint.getText().toString().trim().isEmpty()) {
            endpoint.setError("Enter the NUC observer URL"); endpoint.requestFocus(); return;
        }
        if (enabled.isChecked() && token.getText().toString().trim().isEmpty()) {
            token.setError("Enter the independent observer token"); token.requestFocus(); return;
        }
        try {
            new TokenStore(this, "observer-").save(token.getText().toString().trim());
            prefs.edit().putString("observer_endpoint", endpoint.getText().toString().trim())
                    .putBoolean("push_enabled", enabled.isChecked()).putLong("version_reported_at", 0)
                    .putLong("next_version_report_attempt", 0).apply();
            AlertNotifications.ensureChannel(this);
            if (!enabled.isChecked()) {
                androidx.work.WorkManager.getInstance(this).cancelUniqueWork("register-observer-push");
                if (BuildConfig.FIREBASE_CONFIGURED && !FirebaseApp.getApps(this).isEmpty())
                    FirebaseMessaging.getInstance().setAutoInitEnabled(false);
                refreshStatus(); return;
            }
            if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)
                    != android.content.pm.PackageManager.PERMISSION_GRANTED) {
                requestPermissions(new String[]{android.Manifest.permission.POST_NOTIFICATIONS}, 50); return;
            }
            register();
        } catch (Exception ignored) { feedback.setText("Observer settings could not be stored securely."); }
    }

    @Override public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(requestCode, permissions, results);
        if (requestCode == 50 && AlertNotifications.permitted(this)) register(); else refreshStatus();
    }

    private void register() {
        if (!BuildConfig.FIREBASE_CONFIGURED) { refreshStatus(); return; }
        try {
            if (FirebaseApp.getApps(this).isEmpty()) FirebaseApp.initializeApp(this);
            FirebaseMessaging messaging = FirebaseMessaging.getInstance();
            messaging.setAutoInitEnabled(true);
            messaging.getToken().addOnSuccessListener(value -> {
                PushRegistration.register(this, value);
                feedback.setText("Registration requested. Checking the result…");
                status.postDelayed(this::refreshStatus, 2500);
            }).addOnFailureListener(error -> status.setText("Firebase registration failed; check phone Internet and Google Play services."));
        } catch (Exception ignored) { status.setText("Firebase is unavailable on this installation."); }
    }
}
