package com.dwm157.monitor;

import android.content.Context;
import android.content.SharedPreferences;
import androidx.work.Worker;
import androidx.work.WorkerParameters;
import org.json.JSONObject;

/** Durable one-time token registration. It does not poll Gateway health in the background. */
public final class PushRegistrationWorker extends Worker {
    public PushRegistrationWorker(Context context, WorkerParameters parameters) { super(context, parameters); }

    @Override public Result doWork() {
        Context context = getApplicationContext();
        SharedPreferences prefs = context.getSharedPreferences("monitor", Context.MODE_PRIVATE);
        if (!prefs.getBoolean("push_enabled", false)) return Result.success();
        String endpoint = prefs.getString("observer_endpoint", "");
        String token = new TokenStore(context, "observer-").load();
        String fcmToken = new TokenStore(context, "fcm-").load();
        if (endpoint.isEmpty() || token.isEmpty() || fcmToken.isEmpty()) {
            prefs.edit().putString("push_registration_status", "Registration needs an observer URL and token").apply();
            return Result.failure();
        }
        try {
            ApiClient api = new ApiClient();
            api.post(endpoint, token, "/api/v1/push/register", new JSONObject().put("token", fcmToken));
            prefs.edit().putString("push_registration_status", "Registered with observer")
                    .putLong("push_registered_at", System.currentTimeMillis()).apply();
            PushRegistration.reportVersion(context, api, endpoint, token);
            return Result.success();
        } catch (Exception exception) {
            boolean rejected = exception instanceof ApiClient.ApiException &&
                    (((ApiClient.ApiException)exception).status == 401 || ((ApiClient.ApiException)exception).status == 403);
            boolean exhausted = getRunAttemptCount() >= 10;
            prefs.edit().putString("push_registration_status", rejected ? "Registration rejected; check observer token and allowed peer"
                    : exhausted ? "Registration failed repeatedly; open alert settings to retry" : "Registration pending; will retry when the observer is reachable").apply();
            return rejected || exhausted ? Result.failure() : Result.retry();
        }
    }
}
