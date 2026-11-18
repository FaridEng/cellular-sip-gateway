package com.dwm157.monitor;

import android.app.Activity;
import android.content.Context;
import android.graphics.Color;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class HistoryActivity extends Activity {
    private final ExecutorService executor = Executors.newSingleThreadExecutor();
    private final Handler main = new Handler(Looper.getMainLooper());
    private final ApiClient client = new ApiClient();
    private LinearLayout charts;
    private TextView status;
    private final java.util.Map<String, Button> rangeButtons = new java.util.LinkedHashMap<>();
    private String selectedRange = "24h";

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        LinearLayout root = MonitorUi.screen(this, "Monitoring history", "Trends & connection quality · Pacific Time", true);
        List<Button> rangeViews = new ArrayList<>();
        for (String range : new String[]{"1h", "24h", "7d", "30d"}) {
            Button button = MonitorUi.button(this, range, false);
            button.setOnClickListener(v -> load(range));
            rangeViews.add(button); rangeButtons.put(range, button);
        }
        MonitorUi.row(root, rangeViews.toArray(new Button[0]));
        status = MonitorUi.note(root, "Loading history…");
        charts = MonitorUi.column(this); root.addView(charts, params());
        load(state == null ? "24h" : state.getString("range", "24h"));
    }

    private void load(String range) {
        selectedRange = range;
        for (var entry : rangeButtons.entrySet()) entry.getValue().setSelected(entry.getKey().equals(range));
        status.setText("Loading " + range + "…");
        charts.setAlpha(0.5f);
        executor.execute(() -> {
            try {
                var prefs = getSharedPreferences("monitor", Context.MODE_PRIVATE);
                boolean demo = prefs.getBoolean("demo", true);
                JSONObject response = demo ? demoHistory(range) : client.getHistory(prefs.getString("endpoint", ""), new TokenStore(this).load(), range);
                main.post(() -> { if (selectedRange.equals(range)) render(response, demo); });
            } catch (Exception exception) { main.post(() -> {
                if (!selectedRange.equals(range)) return;
                charts.removeAllViews(); charts.setAlpha(1f);
                status.setText("History unavailable. Try another range or refresh.\n" + exception.getMessage());
            }); }
        });
    }

    private void render(JSONObject response, boolean demo) {
        JSONArray points = response.optJSONArray("points"); charts.removeAllViews();
        charts.setAlpha(1f);
        String span = "";
        if (points != null && points.length() > 0) {
            JSONObject first = points.optJSONObject(0), last = points.optJSONObject(points.length() - 1);
            if (first != null && last != null) span = "\n" + PacificTime.format(first.optString("timestampUtc", ""))
                    + " → " + PacificTime.format(last.optString("timestampUtc", ""));
        }
        status.setText((demo ? "Demo · " : "") + (points == null ? 0 : points.length()) + " samples · " + response.optString("range", "") + span);
        add(points, "S25 → Gateway PC RTT (ms)", "s25RttMilliseconds", Color.rgb(35, 118, 175));
        add(points, "Gateway PC → Internet RTT (ms)", "internetRttMilliseconds", Color.rgb(28, 137, 93));
        add(points, "DWM-157 signal (CSQ)", "signalQuality", Color.rgb(185, 113, 30));
        add(points, "Call RTP loss (%)", "callLossPercent", Color.rgb(181, 57, 57));
        add(points, "Call jitter (ms)", "callJitterMilliseconds", Color.rgb(127, 75, 170));
        add(points, "Call RTT (ms)", "callRttMilliseconds", Color.rgb(65, 87, 165));
        add(points, "Galaxy A07 battery (%)", "a07BatteryPercent", Color.rgb(46, 133, 80));
        add(points, "Galaxy A07 temperature (°C)", "a07TemperatureC", Color.rgb(201, 84, 44));
    }

    private void add(JSONArray points, String title, String key, int color) {
        List<Double> values = new ArrayList<>();
        if (points != null) for (int i = 0; i < points.length(); i++) {
            JSONObject point = points.optJSONObject(i); values.add(point == null || point.isNull(key) ? null : point.optDouble(key));
        }
        TelemetryChartView chart = new TelemetryChartView(this); chart.setSeries(title, values, color);
        if (points != null && points.length() > 0) {
            JSONObject first=points.optJSONObject(0), last=points.optJSONObject(points.length()-1);
            if (first != null && last != null) chart.setTimeRange(PacificTime.clock(first.optString("timestampUtc", "")),
                    PacificTime.clock(last.optString("timestampUtc", "")));
        }
        LinearLayout.LayoutParams p = params(); p.setMargins(0, dp(10), 0, 0); charts.addView(chart, p);
    }

    private JSONObject demoHistory(String range) throws Exception {
        long span = "1h".equals(range) ? 3600 : "7d".equals(range) ? 604800 : "30d".equals(range) ? 2592000 : 86400;
        JSONArray points = new JSONArray();
        for (int i=0; i<25; i++) {
            JSONObject point = new JSONObject();
            point.put("timestampUtc", java.time.Instant.now().minusSeconds(span * (24-i) / 24).toString());
            point.put("s25RttMilliseconds", 120 + 30 * Math.sin(i / 2.0));
            point.put("internetRttMilliseconds", 70 + 15 * Math.cos(i / 3.0));
            point.put("signalQuality", 14 + 2 * Math.sin(i / 4.0));
            point.put("callLossPercent", i % 7 == 0 ? 0.2 : 0.0);
            point.put("callJitterMilliseconds", 8 + 3 * Math.sin(i / 2.0));
            point.put("callRttMilliseconds", 160 + 18 * Math.cos(i / 3.0));
            point.put("a07BatteryPercent", 85 - i / 3.0);
            point.put("a07TemperatureC", 31 + Math.sin(i / 4.0));
            points.put(point);
        }
        return new JSONObject().put("range", range).put("points", points);
    }

    @Override protected void onSaveInstanceState(Bundle state) { state.putString("range", selectedRange); super.onSaveInstanceState(state); }

    @Override protected void onDestroy() { executor.shutdownNow(); super.onDestroy(); }
    private LinearLayout.LayoutParams params() { return new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT); }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
}
