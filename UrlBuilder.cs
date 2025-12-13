using System;

namespace GitLive
{
    /// <summary>
    /// Helper class for building URLs with authentication credentials.
    /// </summary>
    public static class UrlBuilder
    {
        /// <summary>
        /// Adds authentication credentials to a URL using UriBuilder.
        /// </summary>
        /// <param name="url">The base URL</param>
        /// <param name="username">Username for authentication (optional)</param>
        /// <param name="password">Password for authentication (optional)</param>
        /// <returns>URL with authentication credentials if provided</returns>
        public static string AddAuthentication(string url, string? username, string? password)
        {
            if (string.IsNullOrWhiteSpace(url))
                return url;

            // If no credentials provided, return original URL
            // Return early only when both username and password are empty
            bool hasUsername = !string.IsNullOrWhiteSpace(username);
            bool hasPassword = !string.IsNullOrWhiteSpace(password);
            
            if (!hasUsername && !hasPassword)
                return url;

            try
            {
                // Parse the URL - try to create a URI first to check if it's valid
                UriBuilder uriBuilder;
                
                // Check if this is a valid URI with a scheme (http, https, ssh, etc.)
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? testUri) || 
                    string.IsNullOrEmpty(testUri.Scheme) || 
                    testUri.Scheme == "file")
                {
                    // Not a valid network URI or is a file URI - don't add authentication
                    return url;
                }

                try
                {
                    uriBuilder = new UriBuilder(url);
                }
                catch (UriFormatException)
                {
                    // If URI parsing fails, return the original URL
                    return url;
                }

                // Add username if provided (use empty string if only password provided)
                if (hasUsername)
                {
                    uriBuilder.UserName = username!;
                }
                else if (hasPassword)
                {
                    // If password is provided without username, use empty username
                    // This allows password-only authentication scenarios
                    uriBuilder.UserName = "";
                }

                // Add password if provided
                if (hasPassword)
                {
                    uriBuilder.Password = password!;
                }

                return uriBuilder.Uri.ToString();
            }
            catch
            {
                // If anything goes wrong, return the original URL
                return url;
            }
        }
    }
}
