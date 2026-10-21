/* The FreeType modules bibui's text engine uses: TrueType outlines (Inter),
   the autofitter (FT_LOAD_FORCE_AUTOHINT) and the smooth rasterizer. */
FT_USE_MODULE( FT_Module_Class, autofit_module_class )
FT_USE_MODULE( FT_Driver_ClassRec, tt_driver_class )
FT_USE_MODULE( FT_Module_Class, psnames_module_class )
FT_USE_MODULE( FT_Module_Class, sfnt_module_class )
FT_USE_MODULE( FT_Renderer_Class, ft_smooth_renderer_class )
