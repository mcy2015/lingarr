export interface ILoginRequest {
    username: string
    password: string
}

export interface IApiKeyResponse {
    apiKey: string
}

/// POST /api/auth/login response: mustChangePassword is true while the
/// seeded default credentials have not been replaced yet (P0-3).
export interface ILoginResponse {
    mustChangePassword: boolean
}

/// GET /api/auth/authenticated response.
export interface IAuthenticatedResponse {
    authenticated: boolean
    authType: string
    mustChangePassword?: boolean
}

/// POST /api/auth/password payload (P0-3). Error bodies carry a machine
/// readable `reason` (invalid_current_password | weak_password | validation |
/// no_session) that the UI maps to localized copy.
export interface IChangePasswordRequest {
    currentPassword: string
    newPassword: string
}
