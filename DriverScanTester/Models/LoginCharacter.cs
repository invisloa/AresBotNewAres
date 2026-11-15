namespace DriverScanTester.Models
{
    /// <summary>
    /// Character the automatic launcher login should log in with. Only the account
    /// username differs between the characters; the login, Phoenix and character-selection
    /// input sequence is identical for all of them.
    /// </summary>
    public enum LoginCharacter
    {
        /// <summary>Knight account (default).</summary>
        Knight,

        /// <summary>Archer account.</summary>
        Archer,

        /// <summary>Mage account.</summary>
        Mage
    }

    public static class LoginCharacterExtensions
    {
        /// <summary>Display name used in the UI and in the startup log lines.</summary>
        public static string ToDisplayName(this LoginCharacter character) => character switch
        {
            LoginCharacter.Mage => "Mage",
            LoginCharacter.Archer => "Archer",
            _ => "Knight"
        };
    }
}
