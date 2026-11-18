package com.dwm157.monitor;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.graphics.Typeface;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;
import java.util.Locale;
import java.util.ArrayDeque;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;

public final class MainActivity extends Activity {
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
    private TextView gateway;
    private TextView readiness;
    private TextView modem;
    private TextView network;
    private TextView networkQuality;
    private TextView system;
    private TextView call;
    private TextView error;
    private TextView versions;
    private TextView observerStatus;
    private TextView events;
    private TextView phone;
    private TextView heroScore;
    private TextView heroCall;
    private TelemetryChartView liveRttChart;
    private String lastSuccessfulUpdate;
    private String lastVersionText = "No previous version observation";
    private final ArrayDeque<ProbeSample> samples = new ArrayDeque<>();

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        preferences = getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        tokenStore = new TokenStore(this);
        AlertNotifications.ensureChannel(this);
        buildUi();
    }

    @Override
    protected void onResume() {
        super.onResume();
        startPolling();
    }

    @Override
    protected void onPause() {
        stopPolling();
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        executor.shutdownNow();
        super.onDestroy();
    }

    private void buildUi() {
        LinearLayout root = MonitorUi.screen(this, "DWM157 Monitor", "Gateway PC overview · Pacific Time", false);
        LinearLayout hero = MonitorUi.card(root);
        banner = MonitorUi.text(this, "Connecting", 14, true);
        banner.setPadding(dp(12), dp(8), dp(12), dp(8));
        hero.addView(banner, new LinearLayout.LayoutParams(-2, -2));
        heroScore = MonitorUi.text(this, "—", 38, true);
        heroScore.setFontFeatureSettings("tnum");
        heroCall = MonitorUi.text(this, "Call readiness / 100", 14, false);
        heroCall.setTextColor(MonitorUi.muted(this));
        MonitorUi.row(hero, heroScore, heroCall);
        updated = MonitorUi.note(hero, "Waiting for first refresh");

        Button refresh = MonitorUi.button(this, "Refresh", true);
        refresh.setOnClickListener(view -> refreshNow());
        Button settings = MonitorUi.button(this, "Settings", false);
        settings.setOnClickListener(view -> showSettings());
        MonitorUi.row(root, refresh, settings);
        Button callLog = MonitorUi.navigation(this, "Call history", android.R.drawable.ic_menu_call);
        callLog.setOnClickListener(view -> startActivity(new Intent(this, CallHistoryActivity.class)));
        Button history = MonitorUi.navigation(this, "Charts", android.R.drawable.ic_menu_sort_by_size);
        history.setOnClickListener(view -> startActivity(new Intent(this, HistoryActivity.class)));
        MonitorUi.row(root, callLog, history);
        Button alerts = MonitorUi.navigation(this, "Alerts & Push", android.R.drawable.ic_popup_reminder);
        alerts.setOnClickListener(view -> startActivity(new Intent(this, AlertsActivity.class)));
        Button diagnostics = MonitorUi.navigation(this, "Diagnostics", android.R.drawable.ic_menu_info_details);
        diagnostics.setOnClickListener(view -> startActivity(new Intent(this, DiagnosticsActivity.class)));
        MonitorUi.row(root, alerts, diagnostics);

        readiness = addCard(root, "Call readiness");
        gateway = addCard(root, "Gateway · Gateway PC");
        call = addCard(root, "Current call");
        networkQuality = addCard(root, "Connection quality · S25 → Gateway PC");
        LinearLayout chartCard = MonitorUi.card(root);
        MonitorUi.note(chartCard, "Request RTT · last 5 minutes");
        liveRttChart = new TelemetryChartView(this);
        chartCard.addView(liveRttChart, matchWrap());
        Button test = MonitorUi.button(this, "Run Internet light test", false);
        test.setOnClickListener(view -> runLightTest());
        MonitorUi.row(chartCard, test);
        network = addCard(root, "Internet & Tailscale");
        observerStatus = addCard(root, "Independent observer · NUC");
        observerStatus.setText("Add the NUC observer in Alerts & Push to receive notifications when this app is closed.");
        events = addCard(root, "Recent connection events");
        events.setText("Configure the observer to see connection events.");
        modem = addCard(root, "DWM-157 & SIM");
        phone = addCard(root, "Galaxy A07 · USB device");
        versions = addCard(root, "Tailscale versions");
        versions.setText("Waiting for version observations");
        system = addCard(root, "Windows system · Gateway PC");
        error = addMutedText(root, "");
        MonitorUi.note(root, "DWM157 Monitor " + BuildConfig.VERSION_NAME + " · All displayed times use Pacific Time");
        showLoading();
    }

    private void startPolling() {
        stopPolling();
        pollingTask = executor.scheduleWithFixedDelay(this::loadSummary, 0, 5, TimeUnit.SECONDS);
    }

    private void stopPolling() {
        if (pollingTask != null) pollingTask.cancel(true);
        pollingTask = null;
    }

    private void refreshNow() {
        executor.execute(this::loadSummary);
    }

    private void loadSummary() {
        try {
            boolean demo = preferences.getBoolean(DEMO, true);
            JSONObject value;
            if (demo) {
                value = readDemo();
            } else {
                ApiClient.TimedResponse response = apiClient.getSummary(
                        preferences.getString(ENDPOINT, ""), tokenStore.load());
                value = response.value;
                addSample(true, response.elapsedMilliseconds);
                JSONObject currentGateway = value.optJSONObject("gateway");
                boolean healthy = currentGateway != null && currentGateway.optBoolean("running", false)
                        && currentGateway.optBoolean("snapshotAvailable", false)
                        && !currentGateway.optBoolean("snapshotStale", true)
                        && currentGateway.optBoolean("sipListening", false);
                String problem = currentGateway != null && !currentGateway.optBoolean("running", false)
                        ? "Gateway service is stopped" : "Gateway telemetry or SIP is not ready";
                LocalConnectionAlerts.observe(this, healthy, problem, "S25 direct observation: " + problem);
            }
            mainHandler.post(() -> render(value, demo));
        } catch (Exception exception) {
            if (!preferences.getBoolean(DEMO, true)) addSample(false, 0);
            if (!preferences.getBoolean(DEMO, true)) {
                String title = "S25 cannot reach Gateway PC";
                String detail = "The direct S25 monitoring request failed; PC, Tailscale, network or monitoring host may be unavailable.";
                if (exception instanceof ApiClient.ApiException) {
                    int status = ((ApiClient.ApiException)exception).status;
                    if (status == 401 || status == 403) {
                        title = "Monitoring access needs attention";
                        detail = "S25 received HTTP " + status + "; review monitoring token and allowed peer configuration.";
                    } else {
                        title = "Monitoring API returned an error";
                        detail = "Gateway PC responded with HTTP " + status + "; Gateway service status is unknown.";
                    }
                } else {
                    android.net.ConnectivityManager manager = getSystemService(android.net.ConnectivityManager.class);
                    android.net.Network active = manager.getActiveNetwork();
                    android.net.NetworkCapabilities capabilities = active == null ? null : manager.getNetworkCapabilities(active);
                    if (capabilities == null || !capabilities.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_INTERNET)) {
                        title = "S25 network unavailable";
                        detail = "This phone has no active Internet-capable network; Gateway status is unknown.";
                    }
                }
                LocalConnectionAlerts.observe(this, false, title, detail);
            }
            mainHandler.post(() -> showFailure(exception.getMessage()));
        } finally {
            if (!preferences.getBoolean(DEMO, true)) loadObserver();
        }
    }

    private void render(JSONObject root, boolean demo) {
        String overall = root.optString("overall", "warning");
        if (overall.isEmpty()) overall = "warning";
        banner.setText((demo ? "Demo · " : "Live · ") + overall.substring(0, 1).toUpperCase(Locale.US) + overall.substring(1));
        int color = "healthy".equals(overall) ? MonitorUi.GOOD
                : "critical".equals(overall) ? MonitorUi.BAD : MonitorUi.WARNING;
        banner.setTextColor(Color.WHITE);
        MonitorUi.status(banner, banner.getText().toString(), color);
        updated.setText("Updated " + PacificTime.format(root.optString("timestampUtc", "")));
        if (!demo) lastSuccessfulUpdate = root.optString("timestampUtc", null);
        error.setText("");
        JSONObject tailscaleVersion = root.optJSONObject("tailscaleVersion");
        lastVersionText = "Gateway PC installed: " + text(tailscaleVersion, "installedVersion")
                + "\nGatewayPc running: " + text(tailscaleVersion, "runningVersion")
                + "\nObserved: " + text(tailscaleVersion, "observedAtUtc")
                + "\nOther devices: configure the independent observer for comparison.";
        versions.setText(lastVersionText);

        JSONObject readinessValue = root.optJSONObject("readiness");
        heroScore.setText(readinessValue == null || readinessValue.isNull("score") ? "—" : readinessValue.optString("score"));
        heroCall.setText("Readiness / 100\n" + text(readinessValue, "state"));
        readiness.setText(lines(
                "State", text(readinessValue, "state"),
                "Score", text(readinessValue, "score") + "/100",
                "Reasons", readinessReasons(readinessValue)));

        JSONObject gatewayValue = root.optJSONObject("gateway");
        gateway.setText(lines(
                "State", text(gatewayValue, "state"),
                "Process", bool(gatewayValue, "running"),
                "SIP listener", bool(gatewayValue, "sipListening"),
                "Registered peers", text(gatewayValue, "registeredPeers"),
                "Version", text(gatewayValue, "version"),
                "Snapshot", snapshot(gatewayValue)));

        JSONObject modemValue = root.optJSONObject("modem");
        modem.setText(lines(
                "Detected", bool(modemValue, "detected"),
                "SIM ready", bool(modemValue, "simReady"),
                "Cellular", bool(modemValue, "cellularRegistered"),
                "Signal CSQ", text(modemValue, "signalQuality"),
                "Operator", text(modemValue, "operatorName")));

        JSONObject networkValue = root.optJSONObject("network");
        JSONObject lightTest = networkValue == null ? null : networkValue.optJSONObject("lastLightTest");
        JSONObject quality = networkValue == null ? null : networkValue.optJSONObject("quality");
        JSONObject peer = quality == null ? null : quality.optJSONObject("s25ToGatewayPc");
        JSONObject internet = quality == null ? null : quality.optJSONObject("internet");
        JSONObject a07 = quality == null ? null : quality.optJSONObject("a07");
        ProbeStats stats = sampleStats();
        networkQuality.setText(lines(
                "S25 → Gateway PC", demo ? milliseconds(peer, "rttMilliseconds") : stats.latestText(),
                "5-min average", demo ? "371.0 ms" : stats.averageText(),
                "Jitter", demo ? "22.0 ms" : stats.jitterText(),
                "Failures", demo ? "0.0%" : stats.failureText(),
                "Quality", demo ? "Poor" : stats.rating(),
                "Tailscale path", peerPath(peer),
                "Gateway PC → Internet", milliseconds(internet, "rttMilliseconds"),
                "Current route", text(quality, "currentRoute"),
                "Previous route", text(quality, "previousRoute"),
                "Route changed", text(quality, "routeChangedAtUtc")));
        phone.setText(lines(
                "USB / ADB", text(a07, "state"),
                "A07 battery", integerUnit(a07, "batteryPercent", "%"),
                "A07 charging", bool(a07, "charging"),
                "A07 temperature", decimalUnit(a07, "temperatureC", " °C"),
                "A07 model", text(a07, "model"),
                "A07 Android", text(a07, "androidVersion")));
        network.setText(lines(
                "Network", bool(networkValue, "networkAvailable"),
                "Route", text(networkValue, "activeRoute"),
                "Tailscale", bool(networkValue, "tailscaleAvailable"),
                "Tailnet IP", text(networkValue, "tailscaleAddress"),
                "Light-test RTT", milliseconds(lightTest, "rttMilliseconds")));

        JSONObject systemValue = root.optJSONObject("system");
        system.setText(lines(
                "Machine", text(systemValue, "machineName"),
                "OS", text(systemValue, "osDescription"),
                "Uptime", duration(systemValue == null ? 0 : systemValue.optLong("uptimeSeconds", 0)),
                "Disk free", gigabytes(systemValue, "systemDriveFreeBytes"),
                "Processors", text(systemValue, "processorCount")));

        JSONObject callValue = root.optJSONObject("call");
        call.setText(lines(
                "State", text(callValue, "state"),
                "Connected", bool(callValue, "connected"),
                "Duration", duration(callValue == null ? 0 : callValue.optLong("durationSeconds", 0)),
                "Codec", text(callValue, "codec"),
                "RTP loss", percent(callValue, "rtpLossPercent"),
                "Jitter", milliseconds(callValue, "jitterMilliseconds"),
                "RTT", milliseconds(callValue, "rttMilliseconds")));
        liveRttChart.setSeries("Request RTT (ms)", sampleValues(), Color.rgb(35, 118, 175));
    }

    private synchronized void addSample(boolean success, double rtt) {
        samples.addLast(new ProbeSample(success, rtt));
        while (samples.size() > 60) samples.removeFirst();
    }

    private synchronized ProbeStats sampleStats() {
        int failures = 0;
        int successes = 0;
        double sum = 0;
        double deltaSum = 0;
        Double previous = null;
        Double latest = null;
        for (ProbeSample sample : samples) {
            if (!sample.success) { failures++; continue; }
            successes++;
            sum += sample.rtt;
            latest = sample.rtt;
            if (previous != null) deltaSum += Math.abs(sample.rtt - previous);
            previous = sample.rtt;
        }
        return new ProbeStats(samples.size(), successes, failures, latest,
                successes == 0 ? null : sum / successes,
                successes < 2 ? null : deltaSum / (successes - 1));
    }

    private synchronized java.util.List<Double> sampleValues() {
        java.util.List<Double> values = new java.util.ArrayList<>();
        for (ProbeSample sample : samples) values.add(sample.success ? sample.rtt : null);
        return values;
    }

    private static String readinessReasons(JSONObject value) {
        if (value == null) return "Unknown";
        org.json.JSONArray reasons = value.optJSONArray("reasons");
        if (reasons == null || reasons.length() == 0) return "No blocking condition";
        StringBuilder result = new StringBuilder();
        for (int i = 0; i < reasons.length(); i++) {
            if (i > 0) result.append(" · ");
            result.append(reasons.optString(i));
        }
        return result.toString();
    }

    private static String peerPath(JSONObject peer) {
        String path = text(peer, "path");
        String region = text(peer, "derpRegion");
        return "DERP".equals(path) && !"Unknown".equals(region) ? path + " (" + region + ")" : path;
    }

    private static final class ProbeSample {
        final boolean success;
        final double rtt;
        ProbeSample(boolean success, double rtt) { this.success = success; this.rtt = rtt; }
    }

    private static final class ProbeStats {
        final int total;
        final int successes;
        final int failures;
        final Double latest;
        final Double average;
        final Double jitter;
        ProbeStats(int total, int successes, int failures, Double latest, Double average, Double jitter) {
            this.total = total; this.successes = successes; this.failures = failures;
            this.latest = latest; this.average = average; this.jitter = jitter;
        }
        String latestText() { return number(latest); }
        String averageText() { return number(average); }
        String jitterText() { return number(jitter); }
        String failureText() { return total == 0 ? "Collecting…" : String.format(Locale.US, "%.1f%% (%d/%d)", failures * 100.0 / total, failures, total); }
        String rating() {
            if (average == null || jitter == null) return "Collecting…";
            double loss = failures * 100.0 / Math.max(1, total);
            if (average < 150 && jitter < 30 && loss < 2) return "Good";
            if (average < 350 && jitter < 80 && loss < 10) return "Fair";
            return "Poor";
        }
        private static String number(Double value) { return value == null ? "Collecting…" : String.format(Locale.US, "%.1f ms", value); }
    }

    private void runLightTest() {
        if (preferences.getBoolean(DEMO, true)) {
            Toast.makeText(this, "Light test is disabled in Demo mode", Toast.LENGTH_SHORT).show();
            return;
        }
        Toast.makeText(this, "Running bounded Internet test…", Toast.LENGTH_SHORT).show();
        executor.execute(() -> {
            try {
                JSONObject result = apiClient.runLightTest(
                        preferences.getString(ENDPOINT, ""), tokenStore.load());
                mainHandler.post(() -> Toast.makeText(this,
                        result.optBoolean("success") ? "Internet reachable" : "Internet test failed",
                        Toast.LENGTH_LONG).show());
                loadSummary();
            } catch (Exception exception) {
                mainHandler.post(() -> Toast.makeText(this, exception.getMessage(), Toast.LENGTH_LONG).show());
            }
        });
    }

    private void showSettings() {
        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(20), dp(8), dp(20), dp(16));

        EditText endpoint = MonitorUi.field(panel, "Gateway PC monitoring URL", false);
        endpoint.setHint("http://TAILSCALE-IP:5088");
        endpoint.setText(preferences.getString(ENDPOINT, ""));
        EditText token = MonitorUi.field(panel, "Monitoring token", true);
        token.setText(tokenStore.load());

        CheckBox demo = new CheckBox(this);
        demo.setText("Use Demo data");
        demo.setChecked(preferences.getBoolean(DEMO, true));
        panel.addView(demo, matchWrap());

        new AlertDialog.Builder(this)
                .setTitle("Monitoring settings")
                .setView(panel)
                .setNegativeButton("Cancel", null)
                .setPositiveButton("Save", (dialog, which) -> {
                    try {
                        tokenStore.save(token.getText().toString());
                        preferences.edit()
                                .putString(ENDPOINT, endpoint.getText().toString().trim())
                                .putBoolean(DEMO, demo.isChecked())
                                .apply();
                        refreshNow();
                    } catch (Exception exception) {
                        Toast.makeText(this, "Token could not be stored", Toast.LENGTH_LONG).show();
                    }
                })
                .show();
    }

    private JSONObject readDemo() throws Exception {
        StringBuilder body = new StringBuilder();
        try (BufferedReader reader = new BufferedReader(new InputStreamReader(
                getAssets().open("demo-summary.json"), StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) body.append(line);
        }
        JSONObject result = new JSONObject(body.toString());
        result.put("timestampUtc", java.time.Instant.now().toString());
        return result;
    }

    private void loadObserver() {
        String endpoint = preferences.getString("observer_endpoint", "");
        if (endpoint.isEmpty()) return;
        try {
            JSONObject result = apiClient.getObserver(endpoint, new TokenStore(this, "observer-").load());
            preferences.edit().putString("observer_cached", result.toString()).apply();
            mainHandler.post(() -> renderObserver(result, false));
            PushRegistration.reportVersion(this, apiClient, endpoint, new TokenStore(this, "observer-").load());
        } catch (Exception exception) {
            String cached = preferences.getString("observer_cached", "");
            if (!cached.isEmpty()) {
                try {
                    JSONObject value = new JSONObject(cached);
                    mainHandler.post(() -> renderObserver(value, true));
                } catch (Exception ignored) { }
            } else mainHandler.post(() -> observerStatus.setText("Observer unreachable from S25. No previous observer data is available."));
        }
    }

    private void renderObserver(JSONObject value, boolean cached) {
        boolean stale = cached;
        try { stale |= System.currentTimeMillis() - java.time.Instant.parse(value.getString("timestampUtc")).toEpochMilli() > 90_000L; }
        catch (Exception ignored) { stale = true; }
        JSONObject probe = value.optJSONObject("probe"), push = value.optJSONObject("push");
        String pushError = push == null || push.isNull("error") ? "" : push.optString("error", "");
        String pushState = push == null || !push.optBoolean("configured") ? "Setup needed"
                : push.optInt("registeredDevices", 0) == 0 ? "Waiting for phone registration"
                : pushError.isEmpty() ? "Enabled · phone registered" : pushError;
        observerStatus.setText(MonitorUi.rows(this, (stale ? "Last observed · cached data" : "Live · NUC viewpoint")
                + "\nStatus: " + text(probe, "code").replace('_', ' ')
                + "\nObserved: " + text(value, "timestampUtc")
                + "\nLast response: " + text(value, "lastGatewayResponseUtc")
                + "\nPush: " + pushState
                + "\nLast alert sent: " + text(push, "lastSuccessUtc")
                + "\n\nNUC observations are separate from this phone's connection."));
        StringBuilder eventText = new StringBuilder(stale ? "Last observed events\n\n" : "");
        org.json.JSONArray recent = value.optJSONArray("recentEvents");
        if (recent == null || recent.length() == 0) eventText.append("No connection events recorded yet.");
        else for (int i = 0; i < Math.min(4, recent.length()); i++) {
            JSONObject item = recent.optJSONObject(i);
            if (item == null) continue;
            if (i > 0) eventText.append("\n\n");
            eventText.append(item.optString("title", "Connection event")).append("\n")
                    .append(PacificTime.format(item.optString("timestampUtc", "")));
        }
        events.setText(eventText);
        StringBuilder content = new StringBuilder(stale ? "LAST OBSERVED INVENTORY\n" : "Observed version inventory\n");
        org.json.JSONArray devices = value.optJSONArray("versions");
        if (devices != null) for (int i = 0; i < devices.length(); i++) {
            JSONObject device = devices.optJSONObject(i);
            if (device == null) continue;
            content.append('\n').append(text(device, "name")).append(" · ").append(text(device, "os"))
                    .append("\nInstalled: ").append(text(device, "installedVersion"))
                    .append("\nRunning: ").append(text(device, "runningVersion"))
                    .append("\nObserved: ").append("not-observed".equals(device.optString("source")) ? "Not yet observed" : text(device, "observedAtUtc"))
                    .append("\nSource: ").append(text(device, "source")).append('\n');
            content.append("Configured track: ").append(text(device, "track")).append('\n');
            String warning = device.optString("warning", "");
            if (!warning.isEmpty()) content.append("WARNING: ").append(warning).append('\n');
        }
        if (!value.isNull("versionInventoryError")) content.append("\nInventory note: ").append(text(value, "versionInventoryError"));
        lastVersionText = content.toString();
        versions.setText(lastVersionText);
    }

    private void showLoading() {
        banner.setText("CONNECTING");
        banner.setTextColor(Color.WHITE);
        MonitorUi.status(banner, "Connecting", MonitorUi.muted(this));
    }

    private void showFailure(String message) {
        banner.setText("OFFLINE");
        banner.setTextColor(Color.WHITE);
        MonitorUi.status(banner, "Offline", MonitorUi.BAD);
        updated.setText(lastSuccessfulUpdate == null
                ? "Last successful update: Never"
                : "Last update " + PacificTime.format(lastSuccessfulUpdate));
        clearLiveData();
        versions.setText("Last observed; current Gateway access failed.\n" + lastVersionText);
        error.setText("Last error: " + (message == null ? "unknown error" : message));
    }

    private void clearLiveData() {
        heroScore.setText("—");
        heroCall.setText("Readiness unknown\nConnection unavailable");
        readiness.setText("No live data — offline");
        gateway.setText("No live data — offline");
        modem.setText("No live data — offline");
        phone.setText("No live data — offline");
        networkQuality.setText("No live data — offline");
        network.setText("No live data — offline");
        system.setText("No live data — offline");
        call.setText("No live data — offline");
        liveRttChart.setSeries("Request RTT (ms)", sampleValues(), Color.rgb(176, 52, 52));
    }

    private TextView addCard(LinearLayout root, String heading) {
        return MonitorUi.section(root, heading);
    }

    private TextView addMutedText(LinearLayout root, String value) {
        return MonitorUi.note(root, value);
    }

    private CharSequence lines(String... values) {
        StringBuilder result = new StringBuilder();
        for (int index = 0; index + 1 < values.length; index += 2) {
            if (result.length() > 0) result.append('\n');
            result.append(values[index]).append(": ").append(values[index + 1]);
        }
        return MonitorUi.rows(this, result.toString());
    }

    private static String text(JSONObject value, String key) {
        if (value == null || value.isNull(key)) return "Unknown";
        String result = value.optString(key, "Unknown");
        if (key.endsWith("Utc")) return PacificTime.format(result);
        return result.isEmpty() ? "Unknown" : result;
    }

    private static String bool(JSONObject value, String key) {
        if (value == null || value.isNull(key)) return "Unknown";
        return value.optBoolean(key) ? "Yes" : "No";
    }

    private static String snapshot(JSONObject value) {
        if (value == null || !value.optBoolean("snapshotAvailable", false)) return "Unavailable";
        return value.optBoolean("snapshotStale", false) ? "Stale" : "Current";
    }

    private static String duration(long seconds) { return PacificTime.duration(seconds); }

    private static String gigabytes(JSONObject value, String key) {
        if (value == null || value.isNull(key)) return "Unknown";
        return String.format(Locale.US, "%.1f GB", value.optLong(key) / 1073741824.0);
    }

    private static String milliseconds(JSONObject value, String key) {
        if (value == null || value.isNull(key)) return "Unknown";
        return String.format(Locale.US, "%.1f ms", value.optDouble(key));
    }

    private static String percent(JSONObject value, String key) {
        if (value == null || value.isNull(key)) return "Unknown";
        return String.format(Locale.US, "%.3f%%", value.optDouble(key));
    }

    private static String integerUnit(JSONObject value, String key, String unit) {
        if (value == null || value.isNull(key)) return "Unknown";
        return value.optInt(key) + unit;
    }

    private static String decimalUnit(JSONObject value, String key, String unit) {
        if (value == null || value.isNull(key)) return "Unknown";
        return String.format(Locale.US, "%.1f%s", value.optDouble(key), unit);
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
