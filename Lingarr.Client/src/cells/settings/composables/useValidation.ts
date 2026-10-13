import { ref, computed } from 'vue'
import { isValidCron } from 'cron-validator'
import i18n from '@/i18n'

type ValidationProps = {
    validationType?: 'number' | 'string' | 'url' | 'cron'
    minLength?: number
    maxLength?: number
    errorMessage?: string
}

export default function useValidation(props: ValidationProps) {
    const isValid = ref(false)
    const error = ref('')

    const isInvalid = computed(() => !isValid.value && error.value !== '')

    const validate = (value: string) => {
        switch (props.validationType) {
            case 'number':
                isValid.value = !isNaN(Number(value))
                error.value = isValid.value
                    ? ''
                    : props.errorMessage || i18n.global.t('toast.validation.invalidNumber')
                break
            case 'string':
                const min = props.minLength ?? 0
                const max = props.maxLength ?? Infinity
                isValid.value = value.length >= min && value.length <= max
                error.value = isValid.value
                    ? ''
                    : props.errorMessage?.format({ minLength: min, maxLength: max }) ||
                      i18n.global.t('toast.validation.lengthBetween', {
                          min,
                          max: max === Infinity ? '∞' : max
                      })
                break
            case 'url':
                const urlPattern = /^(http:\/\/|https:\/\/)[\w-]+(\.[\w-]+)*(:\d+)?(\/.*)?$/
                isValid.value = urlPattern.test(value)
                error.value = isValid.value
                    ? ''
                    : props.errorMessage || i18n.global.t('toast.validation.invalidUrl')
                break
            case 'cron':
                isValid.value = isValidCron(value.trim(), {
                    seconds: false,
                    alias: true,
                    allowBlankDay: true
                })
                error.value = isValid.value
                    ? ''
                    : props.errorMessage || i18n.global.t('toast.validation.invalidCron')
                break
            default:
                isValid.value = true
                error.value = ''
        }
    }

    return { isValid, isInvalid, error, validate }
}
