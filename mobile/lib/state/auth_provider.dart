import 'package:flutter/foundation.dart';

import '../api/api_client.dart';
import 'token_store.dart';

/// Thrown when a non-Student account tries to sign in — this app is Student-only
/// (DOCS §01/02: Librarian/StoreOfficer/Admin use the React dashboard).
class WrongRoleException implements Exception {
  final String message;
  WrongRoleException(this.message);

  @override
  String toString() => message;
}

const _accessTokenKey = 'access_token';
const _refreshTokenKey = 'refresh_token';

/// Session state for the Student role (ADR-2: Provider, least boilerplate). Both the access and
/// refresh tokens are persisted via [TokenStore] — the platform secure store (Keychain/Keystore)
/// in production, never plain SharedPreferences — so the app can restore a session across restarts.
class AuthProvider extends ChangeNotifier {
  final TokenStore _tokenStore;
  final ApiClient _apiClient;

  static const sessionExpiredMessage =
      'Your session expired, please sign in again';

  String? _accessToken;
  String? _refreshToken;
  String? _studentName;
  String? _studentEmail;
  String? _signedOutReason;

  /// The refresh in flight, shared by every call that hits a 401 at the same time.
  Future<bool>? _refreshing;

  AuthProvider({TokenStore? tokenStore, ApiClient? apiClient})
      : _tokenStore = tokenStore ?? const SecureTokenStore(),
        _apiClient = apiClient ?? ApiClient() {
    _apiClient.onUnauthorized = refreshAfterUnauthorized;
  }

  bool get isAuthenticated => _accessToken != null;
  String? get studentName => _studentName;
  String? get studentEmail => _studentEmail;

  /// Why the user is looking at the sign-in screen, when it was not their choice (the session
  /// could not be refreshed). Cleared by the next sign-in.
  String? get signedOutReason => _signedOutReason;

  /// AUDIT C-05: the access token lives 30 minutes. On a 401 the API client calls this once; it
  /// exchanges the stored refresh token for a new pair, and every concurrent caller waits on the
  /// same exchange. If the refresh token is rejected the session ends and the sign-in screen says
  /// why; a network failure keeps the session (the original call's error is shown instead).
  Future<bool> refreshAfterUnauthorized() =>
      _refreshing ??= _refresh().whenComplete(() => _refreshing = null);

  Future<bool> _refresh() async {
    final refreshToken = _refreshToken ?? await _tokenStore.read(_refreshTokenKey);
    if (refreshToken == null) {
      await _expireSession();
      return false;
    }
    try {
      final response = await _apiClient.post('/api/auth/refresh', body: {
        'refreshToken': refreshToken,
      }) as Map<String, dynamic>;
      final user = response['user'] as Map<String, dynamic>;
      await _applySession(
        accessToken: response['accessToken'] as String,
        refreshToken: response['refreshToken'] as String,
        studentName: user['fullName'] as String,
        studentEmail: user['email'] as String,
      );
      return true;
    } on ApiException catch (e) {
      if (e.status == 0) return false; // offline or timed out: keep the session
      await _expireSession();
      return false;
    }
  }

  Future<void> _expireSession() async {
    if (_accessToken != null) _signedOutReason = sessionExpiredMessage;
    await _clearSession();
  }

  /// Shared with the other feature providers (see main.dart) so they always send whatever access
  /// token is currently active — logging in/out updates this same instance's token in place.
  ApiClient get apiClient => _apiClient;

  Future<void> login(String email, String password) async {
    final response = await _apiClient.post('/api/auth/login', body: {
      'email': email,
      'password': password,
    }) as Map<String, dynamic>;

    final user = response['user'] as Map<String, dynamic>;
    if (user['role'] != 'Student') {
      throw WrongRoleException(
          "This account can't sign in to the mobile app. Use the StudyHive staff dashboard instead.");
    }

    await _applySession(
      accessToken: response['accessToken'] as String,
      refreshToken: response['refreshToken'] as String,
      studentName: user['fullName'] as String,
      studentEmail: user['email'] as String,
    );
  }

  Future<void> register({
    required String fullName,
    required String email,
    required String department,
    required int yearOfStudy,
    required String password,
  }) async {
    await _apiClient.post('/api/auth/register', body: {
      'fullName': fullName,
      'email': email,
      'department': department,
      'yearOfStudy': yearOfStudy,
      'password': password,
    });
    await login(email, password);
  }

  /// Attempts to restore a session from storage on app start by exchanging the stored refresh
  /// token for a fresh access token — never trusts a persisted access token blindly, since it may
  /// already have expired.
  Future<void> tryRestoreSession() async {
    final storedRefreshToken = await _tokenStore.read(_refreshTokenKey);
    if (storedRefreshToken == null) return;

    try {
      final response = await _apiClient.post('/api/auth/refresh', body: {
        'refreshToken': storedRefreshToken,
      }) as Map<String, dynamic>;

      final user = response['user'] as Map<String, dynamic>;
      await _applySession(
        accessToken: response['accessToken'] as String,
        refreshToken: response['refreshToken'] as String,
        studentName: user['fullName'] as String,
        studentEmail: user['email'] as String,
      );
    } on ApiException {
      // Expired/revoked — fall through to a clean logged-out state.
      await _clearSession();
    }
  }

  Future<void> logout() async {
    final refreshToken = _refreshToken;
    await _clearSession();
    if (refreshToken != null) {
      // Best-effort: an unreachable API shouldn't block the user from logging out locally.
      try {
        await _apiClient
            .post('/api/auth/logout', body: {'refreshToken': refreshToken});
      } catch (_) {
        // ignored
      }
    }
  }

  Future<void> _applySession({
    required String accessToken,
    required String refreshToken,
    required String studentName,
    required String studentEmail,
  }) async {
    _accessToken = accessToken;
    _refreshToken = refreshToken;
    _studentName = studentName;
    _studentEmail = studentEmail;
    _signedOutReason = null;
    _apiClient.accessToken = accessToken;
    await _tokenStore.write(_accessTokenKey, accessToken);
    await _tokenStore.write(_refreshTokenKey, refreshToken);
    notifyListeners();
  }

  Future<void> _clearSession() async {
    _accessToken = null;
    _refreshToken = null;
    _studentName = null;
    _studentEmail = null;
    _apiClient.accessToken = null;
    await _tokenStore.delete(_accessTokenKey);
    await _tokenStore.delete(_refreshTokenKey);
    notifyListeners();
  }
}
