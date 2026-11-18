package com.dwm157.monitor;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;

import java.nio.charset.StandardCharsets;
import java.security.KeyStore;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

final class TokenStore {
    private static final String KEY_ALIAS = "dwm157-monitor-token";
    private static final String PREFS = "monitor";
    private static final String TOKEN_VALUE = "token_ciphertext";
    private static final String TOKEN_IV = "token_iv";

    private final SharedPreferences preferences;
    private final String namespace;

    TokenStore(Context context) {
        this(context, "");
    }

    TokenStore(Context context, String namespace) {
        this.namespace = namespace;
        preferences = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    void save(String token) throws Exception {
        if (token == null || token.isEmpty()) {
            preferences.edit().remove(namespace + TOKEN_VALUE).remove(namespace + TOKEN_IV).apply();
            return;
        }

        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey());
        byte[] ciphertext = cipher.doFinal(token.getBytes(StandardCharsets.UTF_8));
        preferences.edit()
                .putString(namespace + TOKEN_VALUE, Base64.encodeToString(ciphertext, Base64.NO_WRAP))
                .putString(namespace + TOKEN_IV, Base64.encodeToString(cipher.getIV(), Base64.NO_WRAP))
                .apply();
    }

    String load() {
        String ciphertext = preferences.getString(namespace + TOKEN_VALUE, null);
        String iv = preferences.getString(namespace + TOKEN_IV, null);
        if (ciphertext == null || iv == null) return "";
        try {
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.DECRYPT_MODE, getOrCreateKey(),
                    new GCMParameterSpec(128, Base64.decode(iv, Base64.NO_WRAP)));
            byte[] plaintext = cipher.doFinal(Base64.decode(ciphertext, Base64.NO_WRAP));
            return new String(plaintext, StandardCharsets.UTF_8);
        } catch (Exception ignored) {
            return "";
        }
    }

    private SecretKey getOrCreateKey() throws Exception {
        KeyStore keyStore = KeyStore.getInstance("AndroidKeyStore");
        keyStore.load(null);
        if (keyStore.containsAlias(KEY_ALIAS + namespace)) {
            return ((KeyStore.SecretKeyEntry) keyStore.getEntry(KEY_ALIAS + namespace, null)).getSecretKey();
        }

        KeyGenerator generator = KeyGenerator.getInstance(
                KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
        generator.init(new KeyGenParameterSpec.Builder(
                KEY_ALIAS + namespace,
                KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .build());
        return generator.generateKey();
    }
}
