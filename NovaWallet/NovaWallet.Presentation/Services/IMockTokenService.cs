using NovaWallet.Presentation.DTOs;

namespace NovaWallet.Presentation.Services;

public interface IMockTokenService
{
    TokenResponse Issue(string customerId);
}
