namespace Pagewing.Core.Updates;

/// <summary>A public key the program accepts signatures from.</summary>
/// <param name="Id">The identifier the signature text names; it contains no colon.</param>
/// <param name="PublicKey">The ECDSA P-256 public key as DER <c>SubjectPublicKeyInfo</c>.</param>
public sealed record UpdateKey(string Id, byte[] PublicKey);
