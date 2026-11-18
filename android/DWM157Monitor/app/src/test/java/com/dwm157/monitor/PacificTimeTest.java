package com.dwm157.monitor;

import org.junit.Test;
import java.time.Instant;
import java.util.Locale;
import java.util.TimeZone;
import static org.junit.Assert.*;

public final class PacificTimeTest {
    private static final Instant OCTOBER = Instant.parse("2026-10-28T23:50:00Z");

    @Test public void samePacificDayUsesShortClock() {
        assertEquals("Today · 4:30 PM PDT", PacificTime.format("2026-10-28T23:30:00Z", OCTOBER));
    }
    @Test public void yesterdayIsBasedOnPacificDate() {
        assertEquals("Yesterday · 4:30 PM PDT", PacificTime.format("2026-10-28T23:30:00Z", Instant.parse("2026-10-29T08:30:00Z")));
    }
    @Test public void utcMidnightDoesNotStartANewPacificDay() {
        assertEquals("Today · 4:30 PM PDT", PacificTime.format("2026-10-28T23:30:00Z", Instant.parse("2026-10-29T03:00:00Z")));
    }
    @Test public void springForwardUsesRealZoneRules() {
        Instant now=Instant.parse("2026-03-08T15:00:00Z");
        assertEquals("Today · 1:59 AM PST", PacificTime.format("2026-03-08T09:59:00Z", now));
        assertEquals("Today · 3:00 AM PDT", PacificTime.format("2026-03-08T10:00:00Z", now));
    }
    @Test public void repeatedFallHourIsUnambiguous() {
        Instant now=Instant.parse("2026-11-01T15:00:00Z");
        assertEquals("Today · 1:30 AM PDT", PacificTime.format("2026-11-01T08:30:00Z", now));
        assertEquals("Today · 1:30 AM PST", PacificTime.format("2026-11-01T09:30:00Z", now));
    }
    @Test public void dotNetOffsetAndSevenFractionDigitsAreSupported() {
        assertEquals("Today · 4:30 PM PDT", PacificTime.format("2026-10-28T16:30:49.0068074-07:00", OCTOBER));
    }
    @Test public void historicDateIncludesYearWhenNeeded() {
        assertEquals("Dec 31, 2025 · 11:30 PM PST", PacificTime.format("2026-01-01T07:30:00Z", OCTOBER));
    }
    @Test public void olderSameYearDateIsCompact() {
        assertEquals("Oct 20 · 4:30 PM PDT", PacificTime.format("2026-10-20T23:30:00Z", OCTOBER));
    }
    @Test public void deviceTimezoneAndLocaleDoNotChangePacificDisplay() {
        TimeZone originalZone=TimeZone.getDefault(); Locale originalLocale=Locale.getDefault();
        try {
            TimeZone.setDefault(TimeZone.getTimeZone("Asia/Kolkata")); Locale.setDefault(Locale.FRANCE);
            assertEquals("Today · 4:30 PM PDT", PacificTime.format("2026-10-28T23:30:00Z", OCTOBER));
        } finally { TimeZone.setDefault(originalZone); Locale.setDefault(originalLocale); }
    }
    @Test public void missingAndMalformedValuesAreExplicit() {
        for(String value : new String[]{null, "", "null", "Unknown", "not-a-date", "2026-99-99T00:00Z"})
            assertEquals("Not available", PacificTime.format(value, OCTOBER));
    }
    @Test public void durationsKeepSecondsForShortCallsAndDaysForUptime() {
        assertEquals("0s", PacificTime.duration(-1)); assertEquals("42s", PacificTime.duration(42));
        assertEquals("1m 2s", PacificTime.duration(62)); assertEquals("2h 5m", PacificTime.duration(7500));
        assertEquals("2d 3h", PacificTime.duration(183600));
    }
    @Test public void diagnosticDisplayDoesNotMutateSourceJson() {
        String source="{\"timestampUtc\":\"2026-10-28T23:30:49.0068074Z\",\"status\":\"Ready\"}";
        String visible=PacificTime.readableJson(source);
        assertTrue(visible.contains("4:30 PM PDT")); assertFalse(visible.contains("T23:30"));
        assertTrue(source.contains("2026-10-28T23:30:49.0068074Z"));
        assertTrue(visible.contains("\"status\":\"Ready\""));
    }
}
