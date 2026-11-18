package com.dwm157.monitor;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONObject;

import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class DiagnosticsActivity extends Activity {
    private static final int CREATE_DOCUMENT = 710;
    private final ExecutorService executor = Executors.newSingleThreadExecutor();
    private final Handler main = new Handler(Looper.getMainLooper());
    private final ApiClient client = new ApiClient();
    private TextView content;
    private Button export;
    private String diagnosticJson;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        LinearLayout root = MonitorUi.screen(this, "Diagnostics", "Review & export a read-only snapshot", true);
        Button refresh = MonitorUi.button(this, "Refresh", true); refresh.setOnClickListener(v -> load());
        export = MonitorUi.button(this, "Save JSON", false); export.setEnabled(false); export.setOnClickListener(v -> chooseDestination());
        MonitorUi.row(root, refresh, export);
        MonitorUi.note(root, "Display times use Pacific Time. Saved JSON retains the original source timestamps.");
        content = MonitorUi.section(root, "Technical snapshot");
        content.setTextIsSelectable(true);
        content.setTextSize(13);
        content.setTypeface(android.graphics.Typeface.MONOSPACE);
        load();
    }

    private void load() {
        content.setText("Loading…"); export.setEnabled(false);
        executor.execute(() -> {
            try {
                var prefs = getSharedPreferences("monitor", Context.MODE_PRIVATE);
                JSONObject response = client.getDiagnostics(prefs.getString("endpoint", ""), new TokenStore(this).load());
                diagnosticJson = response.toString(2);
                main.post(() -> { content.setText(PacificTime.readableJson(diagnosticJson)); export.setEnabled(true); });
            } catch (Exception exception) { main.post(() -> content.setText("Diagnostics unavailable: " + exception.getMessage())); }
        });
    }

    private void chooseDestination() {
        Intent intent = new Intent(Intent.ACTION_CREATE_DOCUMENT);
        intent.setType("application/json"); intent.putExtra(Intent.EXTRA_TITLE, "dwm157-diagnostics.json");
        startActivityForResult(intent, CREATE_DOCUMENT);
    }

    @Override protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode != CREATE_DOCUMENT || resultCode != RESULT_OK || data == null || diagnosticJson == null) return;
        Uri destination = data.getData();
        try (OutputStream stream = destination == null ? null : getContentResolver().openOutputStream(destination)) {
            if (stream == null) throw new IllegalStateException("Destination is unavailable");
            stream.write(diagnosticJson.getBytes(StandardCharsets.UTF_8));
            Toast.makeText(this, "Diagnostics saved", Toast.LENGTH_LONG).show();
        } catch (Exception exception) { Toast.makeText(this, "Save failed: " + exception.getMessage(), Toast.LENGTH_LONG).show(); }
    }

    @Override protected void onDestroy() { executor.shutdownNow(); super.onDestroy(); }
    private LinearLayout.LayoutParams params() { return new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT); }
    private LinearLayout.LayoutParams weighted() { return new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1); }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
}
