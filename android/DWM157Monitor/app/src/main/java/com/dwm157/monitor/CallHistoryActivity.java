package com.dwm157.monitor;

import android.app.Activity;
import android.content.Context;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.graphics.Typeface;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;
import java.time.Instant;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;
import java.util.Locale;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;

public final class CallHistoryActivity extends Activity {
    private static final String PREFS = "monitor";
    private static final String ENDPOINT = "endpoint";
    private static final String DEMO = "demo";

    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final ScheduledExecutorService executor = Executors.newSingleThreadScheduledExecutor();
    private final ApiClient apiClient = new ApiClient();
    private SharedPreferences preferences;
    private TokenStore tokenStore;
    private ScheduledFuture<?> pollingTask;
    private TextView banner;
    private TextView updated;
    private LinearLayout records;
    private String lastSuccessfulUpdate;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        preferences = getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        tokenStore = new TokenStore(this);
        buildUi();
    }

    @Override
    protected void onResume() {
        super.onResume();
        pollingTask = executor.scheduleWithFixedDelay(this::load, 0, 10, TimeUnit.SECONDS);
    }

    @Override
    protected void onPause() {
        if (pollingTask != null) pollingTask.cancel(true);
        pollingTask = null;
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        executor.shutdownNow();
        super.onDestroy();
    }

    private void buildUi() {
        LinearLayout root = MonitorUi.screen(this, "Call history", "Last 10 calls · Pacific Time", true);
        banner = MonitorUi.text(this, "Connecting", 14, true);
        banner.setPadding(dp(12), dp(10), dp(12), dp(10));
        root.addView(banner, matchWrap());
        updated = MonitorUi.note(root, "Waiting for first refresh");
        Button refresh = MonitorUi.button(this, "Refresh calls", true);
        refresh.setOnClickListener(view -> executor.execute(this::load));
        MonitorUi.row(root, refresh);
        records = MonitorUi.column(this);
        root.addView(records, matchWrap());
        showConnecting();
    }

    private void load() {
        try {
            boolean demo = preferences.getBoolean(DEMO, true);
            JSONObject response = demo ? readDemo() : apiClient.getCallHistory(
                    preferences.getString(ENDPOINT, ""), tokenStore.load());
            mainHandler.post(() -> render(response, demo));
        } catch (Exception exception) {
            mainHandler.post(() -> showOffline(exception.getMessage()));
        }
    }

    private void render(JSONObject response, boolean demo) {
        String responseTimestamp = response.optString("timestampUtc", Instant.now().toString());
        if (!demo) lastSuccessfulUpdate = responseTimestamp;
        banner.setText(demo ? "DEMO · ONLINE" : "LIVE · ONLINE");
        banner.setTextColor(Color.WHITE);
        MonitorUi.status(banner, demo ? "Demo · Online" : "Live · Online", MonitorUi.GOOD);
        updated.setText("Updated " + PacificTime.format(responseTimestamp));
        records.removeAllViews();
        if (!response.optBoolean("sourceAvailable", false)) {
            addMessage("Call history source is unavailable");
            return;
        }

        JSONArray calls = response.optJSONArray("calls");
        if (calls == null || calls.length() == 0) {
            addMessage("No calls recorded yet");
            return;
        }
        for (int index = 0; index < Math.min(10, calls.length()); index++) {
            JSONObject call = calls.optJSONObject(index);
            if (call != null) addCallCard(index + 1, call);
        }
    }

    private void addCallCard(int index, JSONObject call) {
        LinearLayout container = MonitorUi.card(records);
        TextView title = MonitorUi.text(this, nullableText(call, "direction", "Call") + " · "
                + nullableText(call, "status", "Unknown"), 17, true);
        container.addView(title, matchWrap());
        MonitorUi.note(container, PacificTime.format(call.optString("requestTimestampUtc", "")));
        TextView card = MonitorUi.text(this, "", 15, false);
        card.setLineSpacing(dp(6), 1.05f);
        card.setPadding(0, dp(8), 0, 0);
        String peer = clientName(call.optString("sipPeer", null));
        String duration = call.isNull("durationSeconds") ? "Not connected"
                : formatDuration(call.optLong("durationSeconds"));
        JSONObject media = call.optJSONObject("media");
        card.setText(lines(
                "Number", nullableText(call, "remoteNumber", "Private / Unknown"),
                "Client", peer,
                "Duration", duration,
                "End", nullableText(call, "endReason", "Unknown"),
                "Codec", nullableText(call, "codec", "Unknown"),
                "RTP loss", metric(media, "lossPercent", "%"),
                "RTP jitter", metric(media, "jitterMilliseconds", " ms"),
                "RTP RTT", metric(media, "rttMilliseconds", " ms"),
                "Out-of-order", integerMetric(media, "outOfOrderPackets"),
                "Dropped modem→RTP", integerMetric(media, "modemToRtpDroppedFrames"),
                "Dropped RTP→modem", integerMetric(media, "rtpToModemDroppedFrames")));
        container.addView(card, matchWrap());
    }

    private void showConnecting() {
        banner.setText("CONNECTING");
        banner.setTextColor(Color.WHITE);
        MonitorUi.status(banner, "Connecting", MonitorUi.muted(this));
        records.removeAllViews();
    }

    private void showOffline(String message) {
        banner.setText("OFFLINE");
        banner.setTextColor(Color.WHITE);
        MonitorUi.status(banner, "Offline", MonitorUi.BAD);
        updated.setText(lastSuccessfulUpdate == null
                ? "Last successful update: Never"
                : "Last update " + PacificTime.format(lastSuccessfulUpdate));
        records.removeAllViews();
        addMessage("No live call data — offline\nLast error: "
                + (message == null ? "unknown error" : message));
    }

    private void addMessage(String value) {
        TextView message = new TextView(this);
        message.setText(value);
        message.setTextSize(15);
        message.setTextColor(MonitorUi.muted(this));
        message.setPadding(dp(14), dp(18), dp(14), dp(18));
        records.addView(message, matchWrap());
    }

    private JSONObject readDemo() throws Exception {
        StringBuilder body = new StringBuilder();
        try (BufferedReader reader = new BufferedReader(new InputStreamReader(
                getAssets().open("demo-calls.json"), StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) body.append(line);
        }
        JSONObject result = new JSONObject(body.toString());
        result.put("timestampUtc", Instant.now().toString());
        return result;
    }

    private static String nullableText(JSONObject value, String key, String fallback) {
        if (value == null || value.isNull(key)) return fallback;
        String result = value.optString(key, fallback);
        return result.trim().isEmpty() ? fallback : result;
    }

    private static String metric(JSONObject value, String key, String unit) {
        if (value == null || value.isNull(key)) return "Unknown";
        return String.format(Locale.US, "%.2f%s", value.optDouble(key), unit);
    }

    private static String integerMetric(JSONObject value, String key) {
        if (value == null || value.isNull(key)) return "Unknown";
        return Long.toString(value.optLong(key));
    }

    private static String clientName(String peer) {
        if (peer == null || peer.trim().isEmpty()) return "Not answered / Unknown";
        String address = peer.trim().split(":", 2)[0];
        switch (address) {
            case "100.64.0.14": return "Galaxy S25 Ultra (" + peer + ")";
            case "100.64.0.13": return "Point A NUC (" + peer + ")";
            case "100.64.0.16": return "Point A MicroSIP (" + peer + ")";
            case "100.64.0.18":
            case "100.64.0.11": return "iPhone (" + peer + ")";
            default: return peer;
        }
    }

    private static String formatDuration(long seconds) { return PacificTime.duration(seconds); }

    private CharSequence lines(String... values) {
        StringBuilder result = new StringBuilder();
        for (int index = 0; index + 1 < values.length; index += 2) {
            if (result.length() > 0) result.append('\n');
            result.append(values[index]).append(": ").append(values[index + 1]);
        }
        return MonitorUi.rows(this, result.toString());
    }

    private LinearLayout.LayoutParams matchWrap() {
        return new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT);
    }

    private LinearLayout.LayoutParams weighted() {
        return new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1);
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
