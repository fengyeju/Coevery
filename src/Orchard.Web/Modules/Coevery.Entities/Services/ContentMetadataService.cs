﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Xml.Linq;
using Coevery.Entities.Models;
using Coevery.Entities.ViewModels;
using Orchard;
using Orchard.ContentManagement;
using Orchard.ContentManagement.MetaData;
using Orchard.ContentManagement.MetaData.Builders;
using Orchard.ContentManagement.MetaData.Models;
using Orchard.ContentManagement.MetaData.Services;
using Orchard.Core.Settings.Metadata.Records;
using Orchard.Data;
using IContentDefinitionEditorEvents = Coevery.Entities.Settings.IContentDefinitionEditorEvents;

namespace Coevery.Entities.Services {
    public interface IContentMetadataService : IDependency {
        bool CreateEntity(EditTypeViewModel sourceModel);
        IEnumerable<EntityMetadataPart> GetRawEntities();
        EntityMetadataPart GetEntity(int id);
        EntityMetadataPart GetEntity(string name);
        bool CheckEntityCreationValid(string name, string displayName);
        bool CheckEntityPublished(string name);
        string ConstructEntityName(string entityName);
        string TryDeleteEntity(int id);

        IEnumerable<FieldMetadataRecord> GetFieldsList(int entityId);
        SettingsDictionary ParseSetting(string setting);
        string ConstructFieldName(string entityName, string displayName);
        bool CheckFieldCreationValid(EntityMetadataPart entity, string name, string displayName);
        bool CreateField(EntityMetadataPart entity, AddFieldViewModel viewModel, IUpdateModel updateModel);
        FieldWithEntityInfoModel TryDeleteField(int id);
    }

    public class ContentMetadataService : IContentMetadataService {
        private readonly IOrchardServices _services;
        private readonly ISettingsFormatter _settingsFormatter;
        private readonly IRepository<ContentFieldDefinitionRecord> _fieldDefinitionRepository;
        private readonly IRepository<FieldMetadataRecord> _fieldMetadataRepository;
        private readonly IContentDefinitionEditorEvents _contentDefinitionEditorEvents;
        public ContentMetadataService(
            IOrchardServices services,
            ISettingsFormatter settingsFormatter,
            IRepository<ContentFieldDefinitionRecord> fieldDefinitionRepository,
            IRepository<FieldMetadataRecord> fieldMetadataRepository,
            IContentDefinitionEditorEvents contentDefinitionEditorEvents
            ) {
            _services = services;
            _settingsFormatter = settingsFormatter;
            _fieldDefinitionRepository = fieldDefinitionRepository;
            _fieldMetadataRepository = fieldMetadataRepository;
            _contentDefinitionEditorEvents = contentDefinitionEditorEvents;
        }

        #region Entity Related
        public IEnumerable<EntityMetadataPart> GetRawEntities() {
            return _services.ContentManager.Query<EntityMetadataPart, EntityMetadataRecord>()
                .ForVersion(VersionOptions.Latest).List();
        }

        public bool CheckEntityCreationValid(string name, string displayName) {
            return !_services.ContentManager
                .Query<EntityMetadataPart>(VersionOptions.Latest, "EntityMetadata").List()
                .Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(x.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));
        }

        public bool CheckEntityPublished(string name) {
            return GetEntity(name).ContentItem.VersionRecord.Published;
        }

        public EntityMetadataPart GetEntity(int id) {
            return _services.ContentManager.Get<EntityMetadataPart>(id, VersionOptions.Latest);
        }

        public EntityMetadataPart GetEntity(string name) {
            var entity = _services.ContentManager.Query<EntityMetadataPart, EntityMetadataRecord>()
                                  .ForVersion(VersionOptions.Latest).Where(record => record.Name == name).List();
            if (entity == null || !entity.Any() || entity.Count() != 1) {
                return null;
            }
            return entity.First();
        }

        public string ConstructEntityName(string entityName) {
            var resultName = entityName;
            while (GetEntity(resultName) != null)
                resultName = VersionName(resultName);
            return resultName;
        }

        public bool CreateEntity(EditTypeViewModel sourceModel) {
            var entityDraft = _services.ContentManager.New<EntityMetadataPart>("EntityMetadata");
            var baseFieldSetting = new SettingsDictionary {
                { "DisplayName",sourceModel.FieldLabel },
                { "Storage","Part"},
                { "CoeveryTextFieldSettings.IsDispalyField",bool.TrueString},
                { "CoeveryTextFieldSettings.Required", bool.TrueString},
                { "CoeveryTextFieldSettings.ReadOnly", bool.TrueString},
                { "CoeveryTextFieldSettings.AlwaysInLayout", bool.TrueString},
                { "CoeveryTextFieldSettings.IsSystemField", bool.TrueString},
                { "CoeveryTextFieldSettings.IsAudit", bool.FalseString}
            };
            entityDraft.DisplayName = sourceModel.DisplayName;
            entityDraft.Name = sourceModel.Name;
            //entityDraft.Settings = sourceModel.Settings;

            entityDraft.FieldMetadataRecords.Add(new FieldMetadataRecord {
                Name = sourceModel.FieldName,
                ContentFieldDefinitionRecord = FetchFieldDefinition("CoeveryTextField"),
                Settings = CompileSetting(baseFieldSetting)
            });
            _services.ContentManager.Create(entityDraft, VersionOptions.Draft);
            return true;
        }

        public string TryDeleteEntity(int id) {
            var entity = GetEntity(id);
            if (entity == null) {
                throw new NullReferenceException("Invalid id");
            }
            entity.FieldMetadataRecords.Clear();
            _services.ContentManager.Remove(entity.ContentItem);
            if (!entity.ContentItem.VersionRecord.Published) {
                return null;
            }
            return entity.Name;
        }
        #endregion

        #region Field Related
        public IEnumerable<FieldMetadataRecord> GetFieldsList(int entityId) {
            return GetEntity(entityId).FieldMetadataRecords;
        }

        public SettingsDictionary ParseSetting(string setting) {
            return string.IsNullOrWhiteSpace(setting) ? null
                : _settingsFormatter.Map(XElement.Parse(setting));
        }

        private string CompileSetting(SettingsDictionary settings) {
            return settings == null
                       ? null
                       : _settingsFormatter.Map(settings).ToString();
        }

        public bool CheckFieldCreationValid(EntityMetadataPart entity, string name, string displayName) {
            return !entity.FieldMetadataRecords.Any(
                field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ParseSetting(field.Settings)["DisplayName"], displayName, StringComparison.OrdinalIgnoreCase));
        }

        public string ConstructFieldName(string entityName, string displayName) {
            var entity = GetEntity(entityName);
            if (entity == null) {
                throw new ArgumentException("The entity doesn't exist: " + entityName);
            }
            var resultName = displayName;
            while (entity.FieldMetadataRecords.Any(x => String.Equals(resultName, x.Name, StringComparison.OrdinalIgnoreCase)))
                resultName = VersionName(resultName);
            return resultName;
        }

        public bool CreateField(EntityMetadataPart entity, AddFieldViewModel viewModel, IUpdateModel updateModel) {
            var fieldBase = new ContentPartFieldDefinition(viewModel.Name) {
                DisplayName = viewModel.DisplayName
            };
            var builder = new FieldConfigurerImpl(fieldBase);
            builder.OfType(viewModel.FieldTypeName);
            _contentDefinitionEditorEvents.PartFieldEditorUpdate(builder, updateModel);
            entity.FieldMetadataRecords.Add(new FieldMetadataRecord {
                ContentFieldDefinitionRecord = FetchFieldDefinition(viewModel.FieldTypeName),
                Name = viewModel.Name,
                Settings = CompileSetting(builder.Build().Settings)
            });
            return true;
        }

        public FieldWithEntityInfoModel TryDeleteField(int id) {
            var field = _fieldMetadataRepository.Get(id);
            var entity = _services.ContentManager.Get(field.EntityMetadataRecord.Id,VersionOptions.Latest).As<EntityMetadataPart>();
            if (entity == null) {
                throw new NullReferenceException("Invalid id");
            }
            entity.FieldMetadataRecords.Remove(field);
            if (!entity.ContentItem.VersionRecord.Published) {
                return null;
            }
            return new FieldWithEntityInfoModel {
                EntityId = entity.Id,
                EntityName = entity.Name,
                FieldId = field.Id,
                FieldName = field.Name
            };
        }
        #region Field Private Methods
        private ContentFieldDefinitionRecord FetchFieldDefinition(string fieldType) {
            var baseFieldDefinition = _fieldDefinitionRepository.Get(def => def.Name == fieldType);
            if (baseFieldDefinition == null) {
                baseFieldDefinition = new ContentFieldDefinitionRecord { Name = fieldType };
                _fieldDefinitionRepository.Create(baseFieldDefinition);
            }
            return baseFieldDefinition;
        }

        class FieldConfigurerImpl : ContentPartFieldDefinitionBuilder {
            private ContentFieldDefinition _fieldDefinition;
            private readonly string _fieldName;

            public FieldConfigurerImpl(ContentPartFieldDefinition field)
                : base(field) {
                _fieldDefinition = field.FieldDefinition;
                _fieldName = field.Name;
            }
            public ContentPartFieldDefinition Build() {
                return new ContentPartFieldDefinition(_fieldDefinition, _fieldName, _settings);
            }

            public override string Name {
                get { return _fieldName; }
            }

            public override string FieldType {
                get { return _fieldDefinition.Name; }
            }

            public override ContentPartFieldDefinitionBuilder OfType(ContentFieldDefinition fieldDefinition) {
                _fieldDefinition = fieldDefinition;
                return this;
            }

            public override ContentPartFieldDefinitionBuilder OfType(string fieldType) {
                _fieldDefinition = new ContentFieldDefinition(fieldType);
                return this;
            }
        }
        #endregion
        #endregion

        private static string VersionName(string name) {
            int version;
            var nameParts = name.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);

            if (nameParts.Length > 1 && int.TryParse(nameParts.Last(), out version)) {
                version = version > 0 ? ++version : 2;
                //this could unintentionally chomp something that looks like a version
                name = string.Join("_", nameParts.Take(nameParts.Length - 1));
            } else {
                version = 2;
            }
            return string.Format("{0}_{1}", name, version);
        }
    }
}

/*Abandoned code
 ---------------Entity------------------
 _contentDefinitionService.AlterType(viewModel, this);
            _contentDefinitionService.AddPartToType(viewModel.Name, viewModel.Name);
            _contentDefinitionService.AddPartToType("CoeveryCommonPart", viewModel.Name);

            _contentDefinitionManager.AlterPartDefinition(viewModel.Name,
                builder => builder.WithField(viewModel.FieldName,
                    fieldBuilder => fieldBuilder
                        .OfType("CoeveryTextField")
                        .WithDisplayName(viewModel.FieldLabel)
                        .WithSetting("CoeveryTextFieldSettings.IsDispalyField", bool.TrueString)
                        .WithSetting("CoeveryTextFieldSettings.Required", bool.TrueString)
                        .WithSetting("CoeveryTextFieldSettings.ReadOnly", bool.TrueString)
                        .WithSetting("CoeveryTextFieldSettings.AlwaysInLayout", bool.TrueString)
                        .WithSetting("CoeveryTextFieldSettings.IsSystemField", bool.TrueString)
                        .WithSetting("CoeveryTextFieldSettings.IsAudit", bool.FalseString)
                        .WithSetting("CoeveryTextFieldSettings.HelpText", string.Empty)
                        .WithSetting("Storage", "Part")));

            Services.Notifier.Information(T("The \"{0}\" content type has been created.", viewModel.DisplayName));
            _entityEvents.OnCreated(viewModel.Name);

            _schemaUpdateService.CreateTable(viewModel.Name,
                context => context.FieldColumn(viewModel.FieldName, "CoeveryTextField"));
 ---------------Field------------------ 
 var partViewModel = _contentDefinitionService.GetPart(id);
            var typeViewModel = _contentDefinitionService.GetType(id);

            if (partViewModel == null) {
                // id passed in might be that of a type w/ no implicit field
                if (typeViewModel != null) {
                    partViewModel = new EditPartViewModel {Name = typeViewModel.Name};
                    _contentDefinitionService.AddPart(new CreatePartViewModel {Name = partViewModel.Name});
                    _contentDefinitionService.AddPartToType(partViewModel.Name, typeViewModel.Name);
                }
                else {
                    return HttpNotFound();
                }
            }
  _contentDefinitionService.AddFieldToPart(viewModel.Name, viewModel.DisplayName, viewModel.FieldTypeName, partViewModel.Name);
  _contentDefinitionService.AlterField(partViewModel.Name, viewModel.Name, this);
 _schemaUpdateService.CreateColumn(typeViewModel.Name, viewModel.Name, viewModel.FieldTypeName);
*/