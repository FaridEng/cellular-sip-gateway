package com.dwm157.monitor;

import java.time.Instant;
import java.time.LocalDate;
import java.time.ZoneId;
import java.time.ZonedDateTime;
import java.time.format.DateTimeFormatter;
import java.util.Locale;

/** Display-only formatting. API values and exported evidence retain their original timestamps. */
final class PacificTime {
    static final ZoneId ZONE = ZoneId.of("America/Los_Angeles");
    private static final DateTimeFormatter CLOCK = DateTimeFormatter.ofPattern("h:mm a z", Locale.US);
    private static final DateTimeFormatter DATE = DateTimeFormatter.ofPattern("MMM d", Locale.US);
    private static final DateTimeFormatter YEAR = DateTimeFormatter.ofPattern("MMM d, uuuu", Locale.US);
    private PacificTime() { }

    static String format(String timestamp) { return format(timestamp, Instant.now()); }

    static String format(String timestamp, Instant now) {
        if (timestamp == null || timestamp.trim().isEmpty() || "null".equals(timestamp)) return "Not available";
        try {
            ZonedDateTime time = Instant.parse(timestamp).atZone(ZONE);
            LocalDate today = now.atZone(ZONE).toLocalDate();
            String day = time.toLocalDate().equals(today) ? "Today"
                    : time.toLocalDate().equals(today.minusDays(1)) ? "Yesterday"
                    : time.format(time.getYear() == today.getYear() ? DATE : YEAR);
            return day + " · " + time.format(CLOCK);
        } catch (RuntimeException ignored) { return "Not available"; }
    }

    static String clock(String timestamp) {
        try { return Instant.parse(timestamp).atZone(ZONE).format(CLOCK); }
        catch (RuntimeException ignored) { return "Not available"; }
    }

    static String readableJson(String json) {
        java.util.regex.Matcher values = java.util.regex.Pattern
                .compile("\"(\\d{4}-\\d{2}-\\d{2}T[^\"\\r\\n]+)\"").matcher(json);
        StringBuffer result = new StringBuffer();
        while (values.find()) {
            String time = format(values.group(1));
            values.appendReplacement(result, java.util.regex.Matcher.quoteReplacement(
                    "Not available".equals(time) ? values.group() : "\"" + time + "\""));
        }
        values.appendTail(result);
        return result.toString();
    }

    static String duration(long value) {
        long seconds = Math.max(0, value);
        if (seconds < 60) return seconds + "s";
        if (seconds < 3600) return seconds / 60 + "m " + seconds % 60 + "s";
        if (seconds < 86400) return seconds / 3600 + "h " + (seconds % 3600) / 60 + "m";
        return seconds / 86400 + "d " + (seconds % 86400) / 3600 + "h";
    }
}
