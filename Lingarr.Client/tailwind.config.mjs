/** @type {import('tailwindcss').Config} */
export default {
    content: ['./index.html', './src/**/*.{ts,vue}'],
    theme: {
        extend: {
            fontFamily: {
                sans: [
                    'Archivo Variable',
                    'PingFang SC',
                    'Hiragino Sans GB',
                    'Microsoft YaHei',
                    'Noto Sans SC',
                    'system-ui',
                    'sans-serif'
                ],
                mono: [
                    'JetBrains Mono Variable',
                    'JetBrains Mono',
                    'ui-monospace',
                    'SFMono-Regular',
                    'Menlo',
                    'monospace'
                ]
            },
            colors: {
                primary: 'var(--primary)',
                'primary-content': 'var(--primary-content)',
                secondary: 'var(--secondary)',
                'secondary-content': 'var(--secondary-content)',
                tertiary: 'var(--tertiary)',
                'tertiary-content': 'var(--tertiary-content)',
                accent: 'var(--accent)',
                'accent-content': 'var(--accent-content)',
                line: 'var(--line)',
                success: 'var(--success)',
                'success-content': 'var(--success-content)',
                warning: 'var(--warning)',
                'warning-content': 'var(--warning-content)',
                danger: 'var(--danger)',
                'danger-content': 'var(--danger-content)',
                info: 'var(--info)',
                'info-content': 'var(--info-content)',
                'track-a': 'var(--track-a)',
                'track-b': 'var(--track-b)'
            },
            keyframes: {
                loading: {
                    '0%': {
                        transform: 'translateX(0) scaleX(0)'
                    },
                    '40%': {
                        transform: 'translateX(0) scaleX(0.4)'
                    },
                    '100%': {
                        transform: 'translateX(100%) scaleX(0.5)'
                    }
                },
                ellipsis: {
                    '0%': { opacity: 0 },
                    '50%': { opacity: 1 },
                    '100%': { opacity: 0 }
                },
                blink: {
                    '50%': { opacity: 0 }
                }
            },
            animation: {
                loading: 'loading 1.5s infinite linear',
                ellipsis: 'ellipsis 1.5s infinite',
                blink: 'blink 1.2s steps(1) infinite'
            }
        }
    },

    plugins: [
        function ({ addUtilities }) {
            addUtilities({
                '.mask-gradient': {
                    'mask-image':
                        'linear-gradient(to bottom, rgba(0, 0, 0, 0) 0%, rgba(0, 0, 0, 0.7) 100%)',
                    '-webkit-mask-image':
                        'linear-gradient(to bottom, rgba(0, 0, 0, 0) 0%, rgba(0, 0, 0, 0.7) 100%)'
                },
                '.animation-delay-300': {
                    'animation-delay': '0.3s'
                },
                '.animation-delay-600': {
                    'animation-delay': '0.6s'
                }
            })
        }
    ]
}
