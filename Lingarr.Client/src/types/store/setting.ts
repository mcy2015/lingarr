import { IEncryptedSettings, ISettings } from '@/types'

export interface IUseSettingStore {
    settings: ISettings
    encrypted_settings: IEncryptedSettings
}
