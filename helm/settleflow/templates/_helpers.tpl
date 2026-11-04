{{/* Environment shared by every SettleFlow container. */}}
{{- define "settleflow.env" -}}
- name: ConnectionStrings__Postgres
  valueFrom: { secretKeyRef: { name: {{ .root.Values.postgresSecret }}, key: postgres } }
- name: Kafka__BootstrapServers
  value: {{ .root.Values.kafka.bootstrapServers | quote }}
- name: Kafka__GroupId
  value: {{ .svc.groupId | quote }}
- name: Database__MigrateOnStartup
  value: {{ (not .root.Values.migrations.enabled) | quote }}
{{- with .root.Values.otlpEndpoint }}
- name: OTEL_EXPORTER_OTLP_ENDPOINT
  value: {{ . | quote }}
{{- end }}
{{- with .root.Values.lokiUrl }}
- name: Loki__Url
  value: {{ . | quote }}
{{- end }}
{{- end -}}

{{- define "settleflow.apiSecrets" -}}
- name: Auth__JwtSigningKey
  valueFrom: { secretKeyRef: { name: {{ .Values.postgresSecret }}, key: jwt-signing-key } }
- name: Bootstrap__AdminPassword
  valueFrom: { secretKeyRef: { name: {{ .Values.postgresSecret }}, key: admin-password } }
{{- end -}}
