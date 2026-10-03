import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../state/auth_provider.dart';
import '../theme/app_theme.dart';
import '../widgets/studyhive_ui.dart';
import 'register_screen.dart';

/// M-01 "Sign in" — POST /api/auth/login. The reference shows the failure
/// inline in an accent-bordered panel, never a popup.
class LoginScreen extends StatefulWidget {
  const LoginScreen({super.key});

  @override
  State<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends State<LoginScreen> {
  final _formKey = GlobalKey<FormState>();
  final _emailController = TextEditingController();
  final _passwordController = TextEditingController();

  String? _error;
  bool _submitting = false;

  @override
  void dispose() {
    _emailController.dispose();
    _passwordController.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate()) return;

    setState(() {
      _error = null;
      _submitting = true;
    });

    try {
      await context
          .read<AuthProvider>()
          .login(_emailController.text.trim(), _passwordController.text);
    } on WrongRoleException catch (e) {
      setState(() => _error = e.message);
    } on ApiException catch (e) {
      setState(() => _error = e.toString());
    } catch (_) {
      setState(() => _error = 'Something went wrong. Please try again.');
    } finally {
      if (mounted) setState(() => _submitting = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    // AUDIT C-05: why the app returned here, when the session could not be refreshed.
    final signedOutReason = context.select<AuthProvider, String?>((a) => a.signedOutReason);
    return Scaffold(
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.symmetric(horizontal: 22, vertical: 24),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 390),
              child: Form(
                key: _formKey,
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    const Center(
                        child: Ph(label: 'logo', width: 56, height: 56)),
                    const SizedBox(height: 16),
                    Text('StudyHive',
                        textAlign: TextAlign.center,
                        style: headingStyle(
                            fontSize: 32, height: 1.12, letterSpacing: -0.5)),
                    const SizedBox(height: 4),
                    const Text(
                      'Book a study room in a few taps.',
                      textAlign: TextAlign.center,
                      style: TextStyle(fontSize: 14, color: AppColors.muted),
                    ),
                    const SizedBox(height: 22),
                    ShTextField(
                      label: 'University email',
                      controller: _emailController,
                      keyboardType: TextInputType.emailAddress,
                      autofillHints: const [AutofillHints.email],
                      validator: (value) => (value == null || value.isEmpty)
                          ? 'University email is required'
                          : null,
                    ),
                    const SizedBox(height: 14),
                    ShTextField(
                      label: 'Password',
                      controller: _passwordController,
                      obscureText: true,
                      autofillHints: const [AutofillHints.password],
                      validator: (value) => (value == null || value.isEmpty)
                          ? 'Password is required'
                          : null,
                      onFieldSubmitted: (_) => _submit(),
                    ),
                    if ((_error ?? signedOutReason) != null) ...[
                      const SizedBox(height: 14),
                      InlineError((_error ?? signedOutReason)!),
                    ],
                    const SizedBox(height: 14),
                    PrimaryButton(
                      _submitting ? 'Signing in…' : 'Sign in',
                      onPressed: _submitting ? null : _submit,
                    ),
                    const SizedBox(height: 10),
                    SecondaryButton(
                      'Create an account',
                      onPressed: () => Navigator.of(context).push(
                        MaterialPageRoute(
                            builder: (_) => const RegisterScreen()),
                      ),
                    ),
                    const SizedBox(height: 10),
                    Center(
                      child: ShLink('Forgot password',
                          onPressed: () {}, fontSize: 13),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
