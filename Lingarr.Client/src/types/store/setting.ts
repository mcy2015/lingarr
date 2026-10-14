import { IEncryptedSettingStates, ISettings } from '@/types'

export interface IUseSettingStore {
    settings: ISettings
    /// Write-only mirror of the encrypted settings: per key only whether a
    /// value is configured (server answers booleans, never plaintext).
    encrypted_settings: IEncryptedSettingStates
}
