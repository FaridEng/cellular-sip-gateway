package com.dwm157.monitor;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Path;
import android.util.AttributeSet;
import android.view.View;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

public final class TelemetryChartView extends View {
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final android.text.TextPaint titlePaint = new android.text.TextPaint(Paint.ANTI_ALIAS_FLAG);
    private final Path linePath = new Path();
    private final List<Double> values = new ArrayList<>();
    private String title = "Telemetry";
    private int lineColor = Color.rgb(30, 121, 96);
    private String startLabel = "5 min ago", endLabel = "Now";

    public TelemetryChartView(Context context) { super(context); init(); }
    public TelemetryChartView(Context context, AttributeSet attrs) { super(context, attrs); init(); }
    private void init() {
        setMinimumHeight(dp(190 + 40 * Math.max(0, getResources().getConfiguration().fontScale - 1)));
        setBackground(MonitorUi.rounded(getContext(), MonitorUi.surface(getContext()), Color.TRANSPARENT, 16));
        setImportantForAccessibility(IMPORTANT_FOR_ACCESSIBILITY_YES);
    }

    public void setSeries(String title, List<Double> newValues, int color) {
        this.title = title;
        lineColor = color;
        values.clear();
        values.addAll(newValues);
        Double last = null;
        for (Double value : values) if (value != null && Double.isFinite(value)) last=value;
        setContentDescription(title + ". " + (last == null ? "No samples" : "Latest " + String.format(Locale.US, "%.1f", last)));
        invalidate();
    }

    public void setTimeRange(String first, String last) { startLabel=first; endLabel=last; invalidate(); }

    @Override protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        float left = dp(48), top = dp(48), right = getWidth() - dp(16), bottom = getHeight() - dp(38);
        paint.setTextSize(sp(13)); paint.setColor(MonitorUi.ink(getContext())); paint.setFakeBoldText(true);
        titlePaint.set(paint);
        String visibleTitle = android.text.TextUtils.ellipsize(title, titlePaint,
                Math.max(1, getWidth()-dp(24)), android.text.TextUtils.TruncateAt.END).toString();
        canvas.drawText(visibleTitle, dp(12), dp(26), paint);
        paint.setFakeBoldText(false); paint.setStrokeWidth(dp(1)); paint.setColor(MonitorUi.border(getContext()));
        for (int i = 0; i <= 4; i++) {
            float y = top + (bottom - top) * i / 4f;
            canvas.drawLine(left, y, right, y, paint);
        }
        Double min = null, max = null;
        for (Double value : values) if (value != null && Double.isFinite(value)) {
            min = min == null ? value : Math.min(min, value);
            max = max == null ? value : Math.max(max, value);
        }
        paint.setTextSize(sp(11)); paint.setColor(MonitorUi.muted(getContext()));
        if (min == null || max == null) { canvas.drawText("No samples", left, top + dp(20), paint); return; }
        if (Math.abs(max - min) < 0.001) { max += 1; min -= 1; }
        canvas.drawText(String.format(Locale.US, "%.1f", max), dp(4), top + dp(4), paint);
        canvas.drawText(String.format(Locale.US, "%.1f", min), dp(4), bottom, paint);
        paint.setTextSize(sp(10));
        canvas.drawText(startLabel, left, getHeight()-dp(12), paint);
        canvas.drawText(endLabel, Math.max(left, right-paint.measureText(endLabel)), getHeight()-dp(12), paint);
        paint.setColor(lineColor); paint.setStrokeWidth(dp(2)); paint.setStyle(Paint.Style.STROKE);
        Path path = linePath; path.reset(); boolean segment = false;
        for (int i = 0; i < values.size(); i++) {
            Double value = values.get(i);
            if (value == null || !Double.isFinite(value)) { segment = false; continue; }
            float x = values.size() < 2 ? left : left + (right - left) * i / (values.size() - 1f);
            float y = bottom - (float)((value - min) / (max - min)) * (bottom - top);
            if (!segment) path.moveTo(x, y); else path.lineTo(x, y);
            segment = true;
        }
        canvas.drawPath(path, paint); paint.setStyle(Paint.Style.FILL);
    }

    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    private int dp(float value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    private float sp(float value) { return value * getResources().getDisplayMetrics().scaledDensity; }
}
