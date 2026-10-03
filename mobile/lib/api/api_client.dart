import 'dart:async';
import 'dart:convert';

import 'package:http/http.dart' as http;

/// Base URL of the ASP.NET Core API. Override at build/run time with:
///   flutter run --dart-define=API_BASE_URL=http://10.0.2.2:5299
const String apiBaseUrl = String.fromEnvironment(
  'API_BASE_URL',
  defaultValue: 'http://10.0.2.2:5299',
);

class ApiException implements Exception {
  final int status;
  final String? title;
  final String? detail;

  /// ASP.NET validation problems carry the reason here (field → messages) and no `detail`
  /// (AUDIT C-12), for example `{"Password": ["…minimum length of '8'."]}`.
  final Map<String, List<String>> errors;

  ApiException(this.status, this.title, this.detail, {this.errors = const {}});

  /// The first field message, if the problem had any.
  String? get firstError {
    for (final messages in errors.values) {
      if (messages.isNotEmpty) return messages.first;
    }
    return null;
  }

  @override
  String toString() =>
      detail ?? firstError ?? title ?? 'Request failed with status $status';
}

/// Thin http wrapper: JSON in/out, bearer auth, RFC7807 problem bodies surfaced as ApiException.
///
/// AUDIT C-05: a 401 on any call outside `/api/auth/` asks [onUnauthorized] (wired by
/// AuthProvider) to refresh the session once, then retries the request once with the new access
/// token. A request that was sent with an older token than the current one is simply retried,
/// so concurrent 401s share one refresh.
class ApiClient {
  static const timeoutMessage =
      'StudyHive is taking too long to answer. Check your connection and try again.';
  static const offlineMessage =
      "Can't reach StudyHive. Check your connection and try again.";

  final http.Client _client;
  final Duration timeout;
  String? accessToken;

  /// Refreshes the session after a 401 and reports whether a retry can succeed.
  Future<bool> Function()? onUnauthorized;

  ApiClient({http.Client? client, this.timeout = const Duration(seconds: 15)})
      : _client = client ?? http.Client();

  Future<dynamic> get(String path) => _send('GET', path);

  Future<dynamic> post(String path, {Object? body}) =>
      _send('POST', path, body: body);

  Future<dynamic> put(String path, {Object? body}) =>
      _send('PUT', path, body: body);

  Future<dynamic> delete(String path) => _send('DELETE', path);

  Future<dynamic> _send(String method, String path, {Object? body}) async {
    final sentToken = accessToken;
    var response = await _exchange(method, path, body, sentToken);

    if (response.statusCode == 401 && !path.startsWith('/api/auth/')) {
      final canRetry = accessToken != null && accessToken != sentToken
          ? true // another call already refreshed while this one was in flight
          : await (onUnauthorized?.call() ?? Future.value(false));
      if (canRetry) {
        response = await _exchange(method, path, body, accessToken);
      }
    }

    if (response.statusCode >= 200 && response.statusCode < 300) {
      if (response.body.isEmpty) return null;
      return jsonDecode(response.body);
    }
    throw _problem(response);
  }

  Future<http.Response> _exchange(
      String method, String path, Object? body, String? token) async {
    final request = http.Request(method, Uri.parse('$apiBaseUrl$path'))
      ..headers.addAll({
        'Content-Type': 'application/json',
        if (token != null) 'Authorization': 'Bearer $token',
      });
    if (body != null) {
      request.body = jsonEncode(body);
    }

    try {
      final streamed = await _client.send(request).timeout(timeout);
      return await http.Response.fromStream(streamed).timeout(timeout);
    } on TimeoutException {
      throw ApiException(0, 'Timed out', timeoutMessage);
    } on http.ClientException {
      throw ApiException(0, 'Offline', offlineMessage);
    }
  }

  static ApiException _problem(http.Response response) {
    Map<String, dynamic> problem = {};
    try {
      problem = jsonDecode(response.body) as Map<String, dynamic>;
    } catch (_) {
      // non-JSON error body, fall through with an empty problem map
    }
    final errors = <String, List<String>>{};
    final rawErrors = problem['errors'];
    if (rawErrors is Map<String, dynamic>) {
      rawErrors.forEach((field, messages) {
        if (messages is List) {
          errors[field] = messages.whereType<String>().toList();
        }
      });
    }
    return ApiException(
      response.statusCode,
      problem['title'] as String?,
      problem['detail'] as String?,
      errors: errors,
    );
  }
}
