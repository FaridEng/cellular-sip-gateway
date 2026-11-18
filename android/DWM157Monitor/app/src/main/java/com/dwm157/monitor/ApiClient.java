package com.dwm157.monitor;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.net.HttpURLConnection;
import java.net.InetAddress;
import java.net.URI;
import java.net.URL;
import java.nio.charset.StandardCharsets;

final class ApiClient {
    private static final int MAX_RESPONSE_CHARS = 1024 * 1024;

    TimedResponse getSummary(String baseUrl, String token) throws Exception {
        long started = System.nanoTime();
        JSONObject value = request(baseUrl, token, "/api/v1/summary", "GET");
        return new TimedResponse(value, (System.nanoTime() - started) / 1_000_000.0);
    }

    static final class TimedResponse {
        final JSONObject value;
        final double elapsedMilliseconds;

        TimedResponse(JSONObject value, double elapsedMilliseconds) {
            this.value = value;
            this.elapsedMilliseconds = elapsedMilliseconds;
        }
    }

    JSONObject runLightTest(String baseUrl, String token) throws Exception {
        return request(baseUrl, token, "/api/v1/network/light-test", "POST");
    }

    JSONObject getCallHistory(String baseUrl, String token) throws Exception {
        return request(baseUrl, token, "/api/v1/calls?limit=10", "GET");
    }

    JSONObject getHistory(String baseUrl, String token, String range) throws Exception {
        String safeRange = ("1h".equals(range) || "24h".equals(range) || "7d".equals(range) || "30d".equals(range)) ? range : "24h";
        return request(baseUrl, token, "/api/v1/history?range=" + safeRange + "&maxPoints=720", "GET");
    }

    JSONObject getDiagnostics(String baseUrl, String token) throws Exception {
        return request(baseUrl, token, "/api/v1/diagnostics", "GET");
    }

    JSONObject getObserver(String baseUrl, String token) throws Exception {
        return request(baseUrl, token, "/api/v1/observer", "GET");
    }

    JSONObject post(String baseUrl, String token, String path, JSONObject body) throws Exception {
        return request(baseUrl, token, path, "POST", body);
    }

    static final class ApiException extends IOException {
        final int status;
        ApiException(int status) { super("Monitoring API returned HTTP " + status); this.status = status; }
    }

    private JSONObject request(String baseUrl, String token, String path, String method) throws Exception {
        return request(baseUrl, token, path, method, null);
    }

    private JSONObject request(String baseUrl, String token, String path, String method, JSONObject payload) throws Exception {
        URI base = validateBaseUri(baseUrl);
        String normalized = base.toString().replaceAll("/+$", "");
        HttpURLConnection connection = (HttpURLConnection) new URL(normalized + path).openConnection();
        connection.setRequestMethod(method);
        connection.setInstanceFollowRedirects(false);
        connection.setConnectTimeout(3500);
        connection.setReadTimeout(5000);
        connection.setRequestProperty("Accept", "application/json");
        if (token != null && !token.isEmpty()) connection.setRequestProperty("X-Monitor-Token", token);
        if ("POST".equals(method)) {
            byte[] bytes = payload == null ? new byte[0] : payload.toString().getBytes(StandardCharsets.UTF_8);
            connection.setDoOutput(true);
            connection.setRequestProperty("Content-Type", "application/json");
            connection.setFixedLengthStreamingMode(bytes.length);
            try (java.io.OutputStream output = connection.getOutputStream()) { output.write(bytes); }
        }

        int status = connection.getResponseCode();
        InputStream stream = status >= 200 && status < 300
                ? connection.getInputStream() : connection.getErrorStream();
        String body = readBounded(stream);
        connection.disconnect();
        if (status < 200 || status >= 300) {
            throw new ApiException(status);
        }
        return new JSONObject(body);
    }

    private static URI validateBaseUri(String value) throws Exception {
        if (value == null || value.trim().isEmpty()) throw new IOException("Monitoring URL is not configured");
        URI uri = URI.create(value.trim());
        String scheme = uri.getScheme();
        if (uri.getHost() == null || !("http".equalsIgnoreCase(scheme) || "https".equalsIgnoreCase(scheme))) {
            throw new IOException("Monitoring URL must use http or https and include a host");
        }
        if ("http".equalsIgnoreCase(scheme) && !isPrivateHost(uri.getHost())) {
            throw new IOException("Plain HTTP is restricted to private or Tailscale addresses");
        }
        return uri;
    }

    private static boolean isPrivateHost(String host) throws Exception {
        for (InetAddress address : InetAddress.getAllByName(host)) {
            if (address.isLoopbackAddress() || address.isSiteLocalAddress() || address.isLinkLocalAddress()) return true;
            byte[] bytes = address.getAddress();
            if (bytes.length == 4) {
                int first = bytes[0] & 0xff;
                int second = bytes[1] & 0xff;
                if (first == 100 && second >= 64 && second <= 127) return true;
            } else if ((bytes[0] & 0xfe) == 0xfc) {
                return true;
            }
        }
        return false;
    }

    private static String readBounded(InputStream stream) throws IOException {
        if (stream == null) return "{}";
        StringBuilder result = new StringBuilder();
        try (BufferedReader reader = new BufferedReader(
                new InputStreamReader(stream, StandardCharsets.UTF_8))) {
            char[] buffer = new char[4096];
            int read;
            while ((read = reader.read(buffer)) >= 0) {
                if (result.length() + read > MAX_RESPONSE_CHARS) throw new IOException("API response is too large");
                result.append(buffer, 0, read);
            }
        }
        return result.toString();
    }
}
