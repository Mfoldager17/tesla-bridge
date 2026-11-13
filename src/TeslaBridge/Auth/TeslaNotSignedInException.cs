namespace TeslaBridge.Auth;

/// <summary>There are no Tesla tokens yet: someone has to sign in with Tesla under Setup on the dashboard.</summary>
public sealed class TeslaNotSignedInException()
    : InvalidOperationException("Not signed in with Tesla. Use 'Sign in with Tesla' under Setup on the dashboard.");
