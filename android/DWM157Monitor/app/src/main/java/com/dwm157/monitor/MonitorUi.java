package com.dwm157.monitor;

import android.app.Activity;
import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.graphics.drawable.StateListDrawable;
import android.os.Build;
import android.text.SpannableString;
import android.text.Spanned;
import android.text.style.ForegroundColorSpan;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

/** Shared native components: semantic colors, scaled text, touch targets and system-bar clearance. */
final class MonitorUi {
    static final int GOOD = Color.rgb(24, 105, 79);
    static final int WARNING = Color.rgb(133, 84, 11);
    static final int BAD = Color.rgb(166, 45, 54);
    private MonitorUi() { }

    static int dp(Context context, float value) { return Math.round(value * context.getResources().getDisplayMetrics().density); }
    static int ink(Context c) { return c.getColor(R.color.monitor_ink); }
    static int muted(Context c) { return c.getColor(R.color.monitor_muted); }
    static int surface(Context c) { return c.getColor(R.color.monitor_surface); }
    static int background(Context c) { return c.getColor(R.color.monitor_background); }
    static int border(Context c) { return c.getColor(R.color.monitor_border); }
    static int accent(Context c) { return c.getColor(R.color.monitor_accent); }
    static LinearLayout.LayoutParams wrap() { return new LinearLayout.LayoutParams(-1, -2); }

    static LinearLayout screen(Activity activity, String title, String subtitle, boolean back) {
        ScrollView scroll = new ScrollView(activity);
        scroll.setId(R.id.monitor_scroll);
        scroll.setFillViewport(true);
        scroll.setBackgroundColor(background(activity));
        if (Build.VERSION.SDK_INT >= 30) activity.getWindow().setDecorFitsSystemWindows(false);
        scroll.setOnApplyWindowInsetsListener((view, insets) -> {
            if (Build.VERSION.SDK_INT >= 30) {
                android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars()
                        | WindowInsets.Type.displayCutout() | WindowInsets.Type.ime());
                view.setPadding(bars.left, bars.top, bars.right, bars.bottom);
            } else {
                view.setPadding(insets.getSystemWindowInsetLeft(), insets.getSystemWindowInsetTop(),
                        insets.getSystemWindowInsetRight(), insets.getSystemWindowInsetBottom());
            }
            return insets;
        });
        LinearLayout root = column(activity);
        int gutter = dp(activity, activity.getResources().getConfiguration().screenWidthDp >= 600 ? 32 : 20);
        root.setPadding(gutter, dp(activity, 16), gutter, dp(activity, 28));
        if (back) {
            Button backButton = button(activity, "Back to overview", false);
            backButton.setCompoundDrawablesWithIntrinsicBounds(android.R.drawable.ic_media_previous, 0, 0, 0);
            backButton.setCompoundDrawableTintList(ColorStateList.valueOf(accent(activity)));
            backButton.setOnClickListener(v -> activity.finish());
            LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(-2, -2);
            p.bottomMargin = dp(activity, 12);
            root.addView(backButton, p);
        }
        TextView heading = text(activity, title, 28, true);
        if (Build.VERSION.SDK_INT >= 28) heading.setAccessibilityHeading(true);
        root.addView(heading, wrap());
        note(root, subtitle);
        scroll.addView(root, new ScrollView.LayoutParams(-1, -2));
        activity.setContentView(scroll);
        boolean night = (activity.getResources().getConfiguration().uiMode
                & android.content.res.Configuration.UI_MODE_NIGHT_MASK) == android.content.res.Configuration.UI_MODE_NIGHT_YES;
        if (Build.VERSION.SDK_INT >= 30) {
            android.view.WindowInsetsController controller = activity.getWindow().getInsetsController();
            int flags = android.view.WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS
                    | android.view.WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS;
            if (controller != null) controller.setSystemBarsAppearance(night ? 0 : flags, flags);
        } else if (Build.VERSION.SDK_INT >= 27) {
            View decor = activity.getWindow().getDecorView();
            int flags = View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR | View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR;
            decor.setSystemUiVisibility(night ? decor.getSystemUiVisibility() & ~flags : decor.getSystemUiVisibility() | flags);
            activity.getWindow().setNavigationBarColor(background(activity));
        }
        scroll.requestApplyInsets();
        return root;
    }

    static LinearLayout column(Context c) {
        LinearLayout column = new LinearLayout(c);
        column.setOrientation(LinearLayout.VERTICAL);
        return column;
    }

    static TextView text(Context c, String value, float size, boolean bold) {
        TextView text = new TextView(c);
        text.setText(value);
        text.setTextSize(size);
        text.setTextColor(ink(c));
        text.setTypeface(Typeface.create(bold ? "sans-serif-medium" : "sans-serif", Typeface.NORMAL));
        text.setLineSpacing(dp(c, 3), 1.05f);
        return text;
    }

    static TextView note(LinearLayout root, String value) {
        TextView note = text(root.getContext(), value, 13, false);
        note.setTextColor(muted(root.getContext()));
        LinearLayout.LayoutParams p = wrap();
        p.topMargin = dp(root.getContext(), 6);
        p.bottomMargin = dp(root.getContext(), 6);
        root.addView(note, p);
        return note;
    }

    static GradientDrawable rounded(Context c, int fill, int stroke, int radius) {
        GradientDrawable shape = new GradientDrawable();
        shape.setColor(fill);
        shape.setCornerRadius(dp(c, radius));
        if (stroke != Color.TRANSPARENT) shape.setStroke(dp(c, 1), stroke);
        return shape;
    }

    static LinearLayout card(LinearLayout root) {
        Context c = root.getContext();
        LinearLayout card = column(c);
        card.setBackground(rounded(c, surface(c), border(c), 20));
        card.setPadding(dp(c, 16), dp(c, 16), dp(c, 16), dp(c, 16));
        LinearLayout.LayoutParams p = wrap();
        p.topMargin = dp(c, 12);
        root.addView(card, p);
        return card;
    }

    static TextView section(LinearLayout root, String title) {
        LinearLayout card = card(root);
        TextView heading = text(root.getContext(), title, 15, true);
        if (Build.VERSION.SDK_INT >= 28) heading.setAccessibilityHeading(true);
        card.addView(heading, wrap());
        TextView body = text(root.getContext(), "Waiting for data…", 15, false);
        body.setPadding(0, dp(root.getContext(), 10), 0, 0);
        body.setLineSpacing(dp(root.getContext(), 6), 1.05f);
        body.setFontFeatureSettings("tnum");
        card.addView(body, wrap());
        return body;
    }

    static Button button(Context c, String title, boolean primary) {
        Button button = new Button(c);
        button.setText(title);
        button.setAllCaps(false);
        button.setTextSize(14);
        button.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        button.setMinHeight(dp(c, 48));
        button.setMinimumHeight(dp(c, 48));
        button.setMinWidth(0);
        button.setMinimumWidth(0);
        button.setPadding(dp(c, 12), dp(c, 10), dp(c, 12), dp(c, 10));
        int fill = primary ? GOOD : surface(c);
        int outline = primary ? GOOD : muted(c);
        StateListDrawable states = new StateListDrawable();
        GradientDrawable selectedFocus = rounded(c, GOOD, Color.WHITE, 12);
        selectedFocus.setStroke(dp(c, 2), Color.WHITE);
        states.addState(new int[]{android.R.attr.state_selected, android.R.attr.state_focused}, selectedFocus);
        GradientDrawable focus = rounded(c, fill, primary ? Color.WHITE : accent(c), 12);
        focus.setStroke(dp(c, 2), primary ? Color.WHITE : accent(c));
        states.addState(new int[]{android.R.attr.state_focused}, focus);
        states.addState(new int[]{android.R.attr.state_selected}, rounded(c, GOOD, GOOD, 12));
        states.addState(new int[]{}, rounded(c, fill, outline, 12));
        button.setBackgroundTintList(null);
        button.setBackground(new RippleDrawable(ColorStateList.valueOf(c.getColor(R.color.monitor_ripple)), states,
                rounded(c, Color.WHITE, Color.TRANSPARENT, 12)));
        button.setTextColor(new ColorStateList(new int[][]{new int[]{-android.R.attr.state_enabled},
                new int[]{android.R.attr.state_selected}, new int[]{}},
                new int[]{muted(c), Color.WHITE, primary ? Color.WHITE : ink(c)}));
        return button;
    }

    static Button navigation(Context c, String title, int icon) {
        Button button = button(c, title, false);
        android.graphics.drawable.Drawable drawable = c.getDrawable(icon);
        if (drawable != null) {
            drawable.setBounds(0, 0, dp(c, 20), dp(c, 20));
            drawable.setTint(accent(c));
            button.setCompoundDrawablesRelative(drawable, null, null, null);
            button.setCompoundDrawablePadding(dp(c, 8));
        }
        return button;
    }

    static void row(LinearLayout root, View... views) {
        LinearLayout row = new LinearLayout(root.getContext());
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setBaselineAligned(false);
        row.setGravity(android.view.Gravity.CENTER_VERTICAL);
        LinearLayout.LayoutParams rp = wrap();
        rp.topMargin = dp(root.getContext(), 8);
        root.addView(row, rp);
        for (int i = 0; i < views.length; i++) {
            LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(0, -2, 1);
            if (i < views.length - 1) p.setMarginEnd(dp(root.getContext(), 8));
            row.addView(views[i], p);
        }
    }

    static void status(TextView text, String value, int color) {
        text.setText(value);
        text.setTextColor(Color.WHITE);
        text.setBackground(rounded(text.getContext(), color, Color.TRANSPARENT, 12));
    }

    static EditText field(LinearLayout root, String label, boolean password) {
        Context c = root.getContext();
        TextView caption = text(c, label, 14, true);
        LinearLayout.LayoutParams cp = wrap(); cp.topMargin = dp(c, 14);
        root.addView(caption, cp);
        EditText field = new EditText(c);
        field.setId(View.generateViewId());
        caption.setLabelFor(field.getId());
        field.setSingleLine(true);
        field.setTextSize(16);
        field.setTextColor(ink(c));
        field.setHintTextColor(muted(c));
        field.setMinHeight(dp(c, 56));
        field.setPadding(dp(c, 12), dp(c, 12), dp(c, 12), dp(c, 12));
        field.setInputType(android.text.InputType.TYPE_CLASS_TEXT | (password
                ? android.text.InputType.TYPE_TEXT_VARIATION_PASSWORD : android.text.InputType.TYPE_TEXT_VARIATION_URI));
        field.setBackground(rounded(c, surface(c), muted(c), 12));
        field.setOnFocusChangeListener((v, focus) -> field.setBackground(rounded(c, surface(c), focus ? accent(c) : muted(c), 12)));
        LinearLayout.LayoutParams fp = wrap(); fp.topMargin = dp(c, 8); root.addView(field, fp);
        return field;
    }

    static CharSequence rows(Context c, String content) {
        SpannableString styled = new SpannableString(content);
        int start = 0;
        for (String line : content.split("\n", -1)) {
            int colon = line.indexOf(':');
            if (colon >= 0) styled.setSpan(new ForegroundColorSpan(muted(c)), start, start + colon + 1, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
            start += line.length() + 1;
        }
        return styled;
    }
}
