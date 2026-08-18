import { AuthenticationResult, Configuration, PublicClientApplication } from '@azure/msal-browser';

const msalConfig: Configuration = {
  auth: {
    clientId:  import.meta.env.VITE_AZURE_CLIENT_ID ?? '',
    authority: `https://login.microsoftonline.com/${import.meta.env.VITE_AZURE_TENANT_ID ?? 'common'}`,
    redirectUri: window.location.origin + '/auth/callback',
  },
  cache: {
    cacheLocation: 'localStorage',
    storeAuthStateInCookie: false,
  },
};

export const loginScopes = ['openid', 'profile', 'email'];

export const msalInstance = new PublicClientApplication(msalConfig);

// Populated by initMsal() in main.tsx before React renders.
// Consumed exactly once by AuthCallbackPage after loginRedirect completes.
let _redirectResult: AuthenticationResult | null = null;

export const initMsal = async (): Promise<void> => {
  await msalInstance.initialize();
  _redirectResult = await msalInstance.handleRedirectPromise();
};

export const consumeRedirectResult = (): AuthenticationResult | null => {
  const r = _redirectResult;
  _redirectResult = null;
  return r;
};
