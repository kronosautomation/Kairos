namespace Kairos.Conformance;

/// <summary>What every <see cref="IWorkerAuthenticator"/> must do: map good credentials to exactly their scope and role, and fail closed on the rest.</summary>
public abstract class AuthenticatorConformance
{
    protected static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    protected static readonly TenantScope A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1"));
    protected static readonly TenantScope B = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b2"));

    protected abstract IWorkerAuthenticator CreateAuthenticator();

    /// <summary>Issues a credential valid for <paramref name="lifetime"/> from <see cref="Now"/> that authenticates as the given scope, role and subject.</summary>
    protected abstract Task<Credential> IssueAsync(IWorkerAuthenticator authenticator, TenantScope scope, Role role, string subject, TimeSpan lifetime);

    /// <summary>Revokes the credential; false when the adapter has no revocation (token systems rotate keys instead).</summary>
    protected virtual Task<bool> RevokeAsync(IWorkerAuthenticator authenticator, Credential credential) => Task.FromResult(false);

    /// <summary>The longest lifetime the adapter allows (worker tokens: one hour).</summary>
    protected virtual TimeSpan Lifetime => TimeSpan.FromMinutes(30);

    protected ValueTask<AuthResult> Authenticate(IWorkerAuthenticator a, Credential c, DateTimeOffset? at = null) =>
        a.AuthenticateAsync(c, at ?? Now, CancellationToken.None);

    [Theory]
    [InlineData(Role.Reader)]
    [InlineData(Role.Worker)]
    [InlineData(Role.Operator)]
    [InlineData(Role.Admin)]
    public async Task A_valid_credential_authenticates_as_exactly_its_scope_role_and_subject(Role role)
    {
        IWorkerAuthenticator auth = CreateAuthenticator();
        Credential credential = await IssueAsync(auth, A, role, "ci-bot@example.com", Lifetime);

        AuthResult result = await Authenticate(auth, credential);

        Assert.True(result.Succeeded);
        Assert.Equal(AuthFailure.None, result.Failure);
        Assert.Equal(A, result.Principal!.Scope);
        Assert.Equal(role, result.Principal.Role);
        Assert.Equal("ci-bot@example.com", result.Principal.Subject);
    }

    [Fact]
    public async Task Credentials_of_different_tenants_never_authenticate_as_each_other()
    {
        IWorkerAuthenticator auth = CreateAuthenticator();
        Credential forA = await IssueAsync(auth, A, Role.Admin, "a", Lifetime);
        Credential forB = await IssueAsync(auth, B, Role.Reader, "b", Lifetime);

        Assert.Equal(A, (await Authenticate(auth, forA)).Principal!.Scope);
        Assert.Equal(B, (await Authenticate(auth, forB)).Principal!.Scope);
        Assert.Equal(Role.Reader, (await Authenticate(auth, forB)).Principal!.Role); // B's reader key does not become A's admin
    }

    [Fact]
    public async Task An_expired_credential_is_refused_and_the_boundary_is_exclusive()
    {
        IWorkerAuthenticator auth = CreateAuthenticator();
        Credential credential = await IssueAsync(auth, A, Role.Worker, "w", Lifetime);

        Assert.True((await Authenticate(auth, credential, Now + Lifetime - TimeSpan.FromSeconds(1))).Succeeded);
        AuthResult expired = await Authenticate(auth, credential, Now + Lifetime);
        Assert.False(expired.Succeeded);
        Assert.Equal(AuthFailure.Expired, expired.Failure);
        Assert.Equal(AuthFailure.Expired, (await Authenticate(auth, credential, Now + Lifetime + TimeSpan.FromDays(30))).Failure);
    }

    [Fact]
    public async Task A_revoked_credential_is_refused_when_the_adapter_supports_revocation()
    {
        IWorkerAuthenticator auth = CreateAuthenticator();
        Credential credential = await IssueAsync(auth, A, Role.Operator, "leaver", Lifetime);
        Assert.True((await Authenticate(auth, credential)).Succeeded);
        if (!await RevokeAsync(auth, credential))
        {
            return;
        }

        AuthResult revoked = await Authenticate(auth, credential);
        Assert.False(revoked.Succeeded);
        Assert.Equal(AuthFailure.Revoked, revoked.Failure);
    }

    [Fact]
    public async Task Altering_a_valid_bearer_credential_never_authenticates()
    {
        IWorkerAuthenticator auth = CreateAuthenticator();
        Credential credential = await IssueAsync(auth, A, Role.Admin, "x", Lifetime);
        if (credential is not BearerCredential bearer)
        {
            return;
        }

        for (int i = 0; i < bearer.Secret.Length; i++)
        {
            char[] chars = bearer.Secret.ToCharArray();
            chars[i] = chars[i] == 'A' ? 'B' : 'A';
            AuthResult result = await Authenticate(auth, new BearerCredential(new string(chars)));
            if (new string(chars) == bearer.Secret)
            {
                continue;
            }

            Assert.False(result.Succeeded, $"Changing character {i} still authenticated.");
        }
    }

    [Fact]
    public async Task Garbage_never_throws_and_never_authenticates()
    {
        IWorkerAuthenticator auth = CreateAuthenticator();
        var rng = new Random(7);
        var garbage = new List<Credential>
        {
            new BearerCredential(""), new BearerCredential(" "), new BearerCredential("kak_"), new BearerCredential("kak_."), new BearerCredential("kat_."),
            new BearerCredential("kak_x.y"), new BearerCredential("Bearer abc"), new BearerCredential(new string('A', 100_000)),
            new BearerCredential("null\0byte"), new BearerCredential("kak_éé.é"), new CertificateCredential(""), new CertificateCredential("zz"),
            new CertificateCredential(new string('f', 64)),
        };
        for (int i = 0; i < 300; i++)
        {
            garbage.Add(new BearerCredential(string.Create(rng.Next(0, 80), rng, static (span, r) =>
            {
                for (int k = 0; k < span.Length; k++)
                {
                    span[k] = (char)r.Next(32, 127);
                }
            })));
        }

        foreach (Credential credential in garbage)
        {
            AuthResult result = await Authenticate(auth, credential);
            Assert.False(result.Succeeded);
            Assert.NotEqual(AuthFailure.None, result.Failure);
        }
    }

    [Fact]
    public async Task Failure_results_carry_no_principal_and_do_not_say_why_a_secret_was_wrong()
    {
        IWorkerAuthenticator auth = CreateAuthenticator();
        Credential real = await IssueAsync(auth, A, Role.Admin, "x", Lifetime);
        if (real is not BearerCredential bearer)
        {
            return;
        }

        string wrongSecret = bearer.Secret[..^2] + (bearer.Secret.EndsWith("AA", StringComparison.Ordinal) ? "BB" : "AA");
        AuthResult wrong = await Authenticate(auth, new BearerCredential(wrongSecret));

        Assert.Null(wrong.Principal);
        Assert.DoesNotContain(bearer.Secret, wrong.ToString(), StringComparison.Ordinal);
    }
}
